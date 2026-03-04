using System;
using System.Globalization;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows;
using GrunflexPOS2.Services;

namespace GrunflexPOS2.Views
{
    public partial class ReportesView : UserControl
    {
        private readonly ReporteService _reporteService;
        private readonly CultureInfo _cl = new CultureInfo("es-CL");

        public ReportesView()
        {
            InitializeComponent();

            _reporteService = new ReporteService();

            CargarDatos();
            DibujarGrafico();
            CargarTablas();
        }

        private void CargarDatos()
        {
            var ventasHoy = _reporteService.ObtenerVentasHoy();
            var cantidadVentas = _reporteService.ObtenerCantidadVentasHoy();
            var ventasTotales = _reporteService.ObtenerVentasTotales();

            VentasHoyText.Text = ventasHoy.ToString("C0", _cl);
            CantidadVentasText.Text = cantidadVentas.ToString();
            VentasTotalesText.Text = ventasTotales.ToString("C0", _cl);

            if (cantidadVentas > 0)
                PromedioVentaText.Text = (ventasHoy / cantidadVentas).ToString("C0", _cl);
            else
                PromedioVentaText.Text = "$0";
        }

        private void DibujarGrafico()
        {
            GraficoVentas.Children.Clear();

            var datos = _reporteService.ObtenerVentasUltimos7Dias();

            double anchoBarra = 40;
            double espacio = 50;
            double alturaMaxima = 140;
            double baseGrafico = 160;

            decimal max = datos.Max(x => x.Total);

            for (int i = 0; i < datos.Count; i++)
            {
                var dia = datos[i].Dia;
                var total = datos[i].Total;

                double altura = max == 0 ? 0 : (double)(total / max) * alturaMaxima;

                double x = 30 + i * (anchoBarra + espacio);
                double y = baseGrafico - altura;

                Rectangle barra = new Rectangle
                {
                    Width = anchoBarra,
                    Height = altura,
                    Fill = Brushes.SteelBlue,
                    RadiusX = 4,
                    RadiusY = 4
                };

                Canvas.SetLeft(barra, x);
                Canvas.SetTop(barra, y);

                GraficoVentas.Children.Add(barra);

                TextBlock monto = new TextBlock
                {
                    Text = total.ToString("C0", _cl),
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Width = 80,
                    TextAlignment = TextAlignment.Center
                };

                Canvas.SetLeft(monto, x - 20);
                Canvas.SetTop(monto, y - 20);

                GraficoVentas.Children.Add(monto);

                TextBlock diaText = new TextBlock
                {
                    Text = dia,
                    FontSize = 12,
                    Width = anchoBarra,
                    TextAlignment = TextAlignment.Center
                };

                Canvas.SetLeft(diaText, x);
                Canvas.SetTop(diaText, baseGrafico + 5);

                GraficoVentas.Children.Add(diaText);
            }
        }

        private void CargarTablas()
        {
            GridMetodosPago.ItemsSource = _reporteService.ObtenerVentasPorMetodo();
            GridCajas.ItemsSource = _reporteService.ObtenerVentasPorCaja();
        }

        private void ImprimirReporte_Click(object sender, RoutedEventArgs e)
        {
            ReportePdfService.GenerarReportePDF();
        }
    }
}