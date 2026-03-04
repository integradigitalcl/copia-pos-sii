using System.Diagnostics;

namespace GrunflexPOS2.Services
{
    public static class ReportePdfService
    {
        public static void GenerarReportePDF()
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://www.grunflex.cl",
                UseShellExecute = true
            });
        }
    }
}