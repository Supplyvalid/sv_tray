using System.Runtime.InteropServices;

namespace ShelivoPrintAgent;

/// <summary>
/// Two Win32 mechanisms exist for sending raw bytes to a printer queue, and
/// different drivers only support one of them:
///   1. gdi32 PASSTHROUGH escape -- routes bytes through the normal GDI print
///      pipeline; many consumer/POS receipt-printer drivers require this.
///   2. winspool WritePrinter with datatype "RAW" -- the classic
///      "RawPrinterHelper" recipe; works when the driver's print processor
///      accepts raw bytes directly through the spooler.
/// Confirmed against a real Honeywell Impact/IHR810 printer: WritePrinter
/// fails with ERROR_INVALID_DATATYPE on that printer, while PASSTHROUGH
/// succeeds. So this tries PASSTHROUGH first and falls back to WritePrinter
/// for drivers that only support the classic path. Same dual-path logic as
/// electron/main.js's openCashDrawer and the earlier Node build's
/// rawPrint.js, ported to native C# so there's no PowerShell subprocess or
/// embedded script involved at all.
/// </summary>
internal static class RawPrinter
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct DOCINFOA
    {
        [MarshalAs(UnmanagedType.LPStr)] public string pDocName;
        [MarshalAs(UnmanagedType.LPStr)] public string? pOutputFile;
        [MarshalAs(UnmanagedType.LPStr)] public string pDataType;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DOCINFO
    {
        public int cbSize;
        public string? lpszDocName;
        public string? lpszOutput;
        public string? lpszDatatype;
        public int fwType;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool StartDocPrinter(IntPtr hPrinter, int level, ref DOCINFOA di);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

    [DllImport("gdi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CreateDC(string? lpszDriver, string lpszDevice, string? lpszOutput, IntPtr lpInitData);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int StartDoc(IntPtr hdc, ref DOCINFO lpdi);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int EndDoc(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int StartPage(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int EndPage(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int Escape(IntPtr hdc, int nEscape, int cbInput, byte[] lpvInData, IntPtr lpvOutData);

    private const int PASSTHROUGH = 19;

    private static bool TrySendViaPassthrough(string printerName, byte[] data, out string? failure)
    {
        failure = null;
        IntPtr hdc = CreateDC(null, printerName, null, IntPtr.Zero);
        if (hdc == IntPtr.Zero)
        {
            failure = $"CreateDC failed (error {Marshal.GetLastWin32Error()})";
            return false;
        }
        try
        {
            var di = new DOCINFO { cbSize = Marshal.SizeOf<DOCINFO>(), lpszDocName = "POS Raw Print Job" };
            if (StartDoc(hdc, ref di) <= 0)
            {
                failure = $"StartDoc failed (error {Marshal.GetLastWin32Error()})";
                return false;
            }
            try
            {
                if (StartPage(hdc) <= 0)
                {
                    failure = $"StartPage failed (error {Marshal.GetLastWin32Error()})";
                    return false;
                }
                var buf = new byte[2 + data.Length];
                buf[0] = (byte)(data.Length & 0xFF);
                buf[1] = (byte)((data.Length >> 8) & 0xFF);
                Array.Copy(data, 0, buf, 2, data.Length);
                int result = Escape(hdc, PASSTHROUGH, buf.Length, buf, IntPtr.Zero);
                EndPage(hdc);
                if (result <= 0)
                {
                    failure = $"Escape(PASSTHROUGH) failed (result {result}, error {Marshal.GetLastWin32Error()})";
                    return false;
                }
                return true;
            }
            finally
            {
                EndDoc(hdc);
            }
        }
        finally
        {
            DeleteDC(hdc);
        }
    }

    private static bool TrySendViaWritePrinter(string printerName, byte[] data, out string? failure)
    {
        failure = null;
        if (!OpenPrinter(printerName, out var hPrinter, IntPtr.Zero))
        {
            failure = $"OpenPrinter failed (error {Marshal.GetLastWin32Error()})";
            return false;
        }
        try
        {
            var di = new DOCINFOA { pDocName = "POS Raw Print Job", pDataType = "RAW" };
            if (!StartDocPrinter(hPrinter, 1, ref di))
            {
                failure = $"StartDocPrinter failed (error {Marshal.GetLastWin32Error()})";
                return false;
            }
            try
            {
                if (!StartPagePrinter(hPrinter))
                {
                    failure = $"StartPagePrinter failed (error {Marshal.GetLastWin32Error()})";
                    return false;
                }
                var pBytes = Marshal.AllocHGlobal(data.Length);
                try
                {
                    Marshal.Copy(data, 0, pBytes, data.Length);
                    if (!WritePrinter(hPrinter, pBytes, data.Length, out _))
                    {
                        failure = $"WritePrinter failed (error {Marshal.GetLastWin32Error()})";
                        return false;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(pBytes);
                }
                EndPagePrinter(hPrinter);
                return true;
            }
            finally
            {
                EndDocPrinter(hPrinter);
            }
        }
        finally
        {
            ClosePrinter(hPrinter);
        }
    }

    public static void SendBytes(string printerName, byte[] data)
    {
        if (TrySendViaPassthrough(printerName, data, out var passthroughFailure))
        {
            return;
        }

        if (TrySendViaWritePrinter(printerName, data, out var writePrinterFailure))
        {
            return;
        }

        throw new Exception($"PASSTHROUGH: {passthroughFailure} | WritePrinter: {writePrinterFailure}");
    }
}
