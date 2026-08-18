using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using GrunflexPOS2.Services;

namespace GrunflexPOS2.Views
{
    public partial class DashboardView : UserControl
    {
        private readonly ReporteService _reporte;
        private readonly CultureInfo _cl = new CultureInfo("es-CL");
        private readonly DispatcherTimer _timer;
        /// <summary>Evita CargarDashboard durante InitializeComponent: SelectionChanged del ComboBox puede
        /// dispararse antes de que existan el resto de controles con nombre (p. ej. KpiVentasTitulo).</summary>
        private bool _xamlInicializado;

        public DashboardView() : this(null)
        {
        }

        /// <param name="reportes">Si es null (POS), usa <see cref="App.DbContext"/>.</param>
        public DashboardView(ReporteService? reportes)
        {
            // Antes de InitializeComponent: el ComboBox dispara SelectionChanged y llama CargarDashboard.
            _reporte = reportes ?? new ReporteService();

            InitializeComponent();
            ConfigurarSelectorMesAnio();
            _xamlInicializado = true;

            CargarDashboard();

            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(45)
            };
            _timer.Tick += (_, _) => CargarDashboard();
            _timer.Start();

            Unloaded += (_, _) => _timer.Stop();
        }

        private void CmbPeriodo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_xamlInicializado)
                return;
            PanelMesAnio.Visibility = PeriodoTag() == "Mes" ? Visibility.Visible : Visibility.Collapsed;
            CargarDashboard();
        }

        private void MesAnio_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_xamlInicializado || PeriodoTag() != "Mes")
                return;
            CargarDashboard();
        }

        private void BtnActualizar_Click(object sender, RoutedEventArgs e) =>
            CargarDashboard();

        private void CargarDashboard()
        {
            CargarMetricas();
            CargarResumenHoy();
            DibujarGrafico7Dias();
            DibujarVentasPorHora();
            CargarMetodos();
            DibujarTopProductos();
            ActualizarEstadoNegocio();

            TxtUltimaActualizacion.Text = $"Actualizado {DateTime.Now:HH:mm:ss}";
        }

        private void CargarResumenHoy()
        {
            var ini = DateTime.Today;
            var fin = DateTime.Today.AddDays(1);
            decimal total = _reporte.ObtenerTotalVentasRango(ini, fin);
            int trans = _reporte.ObtenerCantidadVentasRango(ini, fin);
            int unidades = _reporte.ObtenerUnidadesVendidasRango(ini, fin);

            TxtResumenVentasHoy.Text = total.ToString("C0", _cl);
            TxtResumenTransacciones.Text = trans.ToString("N0", _cl);
            TxtResumenUnidades.Text = $"{unidades:N0} u.";
        }

        private string PeriodoTag()
        {
            if (CmbPeriodo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                return tag;
            return "Hoy";
        }

        private void ConfigurarSelectorMesAnio()
        {
            var meses = Enumerable.Range(1, 12)
                .Select(m => new MesItem
                {
                    Numero = m,
                    Nombre = _cl.DateTimeFormat.MonthNames[m - 1]
                })
                .ToList();

            CmbMes.ItemsSource = meses;
            CmbAnio.ItemsSource = Enumerable.Range(DateTime.Today.Year - 5, 8).OrderByDescending(x => x).ToList();

            CmbMes.SelectedValue = DateTime.Today.Month;
            CmbAnio.SelectedItem = DateTime.Today.Year;
            PanelMesAnio.Visibility = Visibility.Collapsed;
        }

        private (DateTime Inicio, DateTime Fin) RangoMesSeleccionado()
        {
            var anio = CmbAnio.SelectedItem is int y ? y : DateTime.Today.Year;
            var mes = CmbMes.SelectedValue is int m ? m : DateTime.Today.Month;
            var inicio = new DateTime(anio, mes, 1);
            return (inicio, inicio.AddMonths(1));
        }

        private void CargarMetricas()
        {
            var hoy = DateTime.Today;
            string periodo = PeriodoTag();

            decimal v1;
            decimal v2;
            int cant;
            string tituloPrincipal;
            string tituloComparacion;

            switch (periodo)
            {
                case "7":
                    {
                        var inicio = hoy.AddDays(-6);
                        var fin = hoy.AddDays(1);
                        v1 = _reporte.ObtenerTotalVentasRango(inicio, fin);
                        v2 = _reporte.ObtenerTotalVentasRango(inicio.AddDays(-7), inicio);
                        cant = _reporte.ObtenerCantidadVentasRango(inicio, fin);
                        tituloPrincipal = "Ventas (7 días)";
                        tituloComparacion = "7 días previos";
                        break;
                    }
                case "Mes":
                    {
                        var (inicio, fin) = RangoMesSeleccionado();
                        var inicioPrev = inicio.AddMonths(-1);
                        v1 = _reporte.ObtenerTotalVentasRango(inicio, fin);
                        v2 = _reporte.ObtenerTotalVentasRango(inicioPrev, inicio);
                        cant = _reporte.ObtenerCantidadVentasRango(inicio, fin);
                        tituloPrincipal = "Ventas mes completo";
                        tituloComparacion = "Mes anterior completo";
                        break;
                    }
                default:
                    {
                        var ini = hoy.Date;
                        var fin = hoy.AddDays(1);
                        v1 = _reporte.ObtenerTotalVentasRango(ini, fin);
                        v2 = _reporte.ObtenerTotalVentasRango(hoy.AddDays(-1), hoy);
                        cant = _reporte.ObtenerCantidadVentasRango(ini, fin);
                        tituloPrincipal = "Ventas hoy";
                        tituloComparacion = "Ventas ayer";
                        break;
                    }
            }

            KpiVentasTitulo.Text = tituloPrincipal;
            KpiComparacionTitulo.Text = tituloComparacion;

            VentasHoyText.Text = v1.ToString("C0", _cl);
            VentasAyerText.Text = v2.ToString("C0", _cl);

            var variacion = v2 == 0 ? 0 : ((v1 - v2) / v2) * 100;
            VariacionText.Text = variacion >= 0 ? $"▲ {variacion:0}%" : $"▼ {variacion:0}%";
            VariacionText.Foreground = variacion >= 0 ? Brushes.ForestGreen : Brushes.IndianRed;

            TicketPromedioText.Text =
                cant > 0 ? (v1 / cant).ToString("C0", _cl) : "$0";
        }

        private void DibujarGrafico7Dias()
        {
            GraficoVentas.Children.Clear();

            var datos = _reporte.ObtenerVentasUltimos7Dias();
            decimal maxData = datos.Count > 0 ? datos.Max(x => x.Total) : 0;
            bool vacio = maxData == 0;

            PanelGrafico7Vacio.Visibility = vacio ? Visibility.Visible : Visibility.Collapsed;
            if (vacio)
                return;

            decimal maxY = maxData <= 0 ? 1000m : Math.Max(1000m, Math.Ceiling(maxData / 250m) * 250m);

            const double cw = 640;
            const double ch = 200;
            double marginLeft = 46;
            double marginRight = 8;
            double marginTop = 10;
            double marginBottom = 30;
            double plotW = cw - marginLeft - marginRight;
            double plotH = ch - marginTop - marginBottom;

            var gridPen = new SolidColorBrush(Color.FromRgb(229, 231, 235));
            for (int g = 0; g <= 4; g++)
            {
                double gy = marginTop + plotH * g / 4.0;
                var line = new Line
                {
                    X1 = marginLeft,
                    X2 = marginLeft + plotW,
                    Y1 = gy,
                    Y2 = gy,
                    Stroke = gridPen,
                    StrokeThickness = g == 4 ? 1 : 0.6
                };
                GraficoVentas.Children.Add(line);

                decimal val = maxY * (4 - g) / 4m;
                var yLbl = new TextBlock
                {
                    Text = val.ToString("C0", _cl),
                    FontSize = 9,
                    Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128))
                };
                Canvas.SetLeft(yLbl, 2);
                Canvas.SetTop(yLbl, gy - 8);
                GraficoVentas.Children.Add(yLbl);
            }

            double denom = Math.Max(1, datos.Count - 1);
            var puntos = new PointCollection();
            for (int i = 0; i < datos.Count; i++)
            {
                double px = marginLeft + plotW * i / denom;
                double ratio = maxY == 0 ? 0 : (double)(datos[i].Total / maxY);
                ratio = Math.Min(1, Math.Max(0, ratio));
                double py = marginTop + plotH - ratio * plotH;
                puntos.Add(new System.Windows.Point(px, py));
            }

            GraficoVentas.Children.Add(new Polyline
            {
                Points = puntos,
                Stroke = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                StrokeThickness = 2.5,
                Fill = Brushes.Transparent
            });

            for (int i = 0; i < datos.Count; i++)
            {
                double px = marginLeft + plotW * i / denom;
                double ratio = maxY == 0 ? 0 : (double)(datos[i].Total / maxY);
                ratio = Math.Min(1, Math.Max(0, ratio));
                double py = marginTop + plotH - ratio * plotH;

                var dot = new Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = Brushes.White,
                    Stroke = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                    StrokeThickness = 2
                };
                GraficoVentas.Children.Add(dot);
                Canvas.SetLeft(dot, px - 4);
                Canvas.SetTop(dot, py - 4);

                var lbl = new TextBlock
                {
                    Text = datos[i].Etiqueta,
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128))
                };
                Canvas.SetLeft(lbl, px - 22);
                Canvas.SetTop(lbl, marginTop + plotH + 4);
                GraficoVentas.Children.Add(lbl);
            }
        }

        private void DibujarVentasPorHora()
        {
            GraficoHoras.Children.Clear();

            var datos = _reporte.ObtenerVentasPorHoraHoy();
            decimal maxData = datos.Count > 0 ? datos.Max(x => x.Total) : 0;
            bool vacio = maxData == 0;

            PanelHorasVacio.Visibility = vacio ? Visibility.Visible : Visibility.Collapsed;
            if (vacio)
                return;

            decimal maxY = maxData <= 0 ? 1000m : Math.Max(1000m, Math.Ceiling(maxData / 250m) * 250m);

            const double cw = 520;
            const double ch = 170;
            double marginLeft = 44;
            double marginRight = 6;
            double marginTop = 8;
            double marginBottom = 26;
            double plotW = cw - marginLeft - marginRight;
            double plotH = ch - marginTop - marginBottom;

            var gridPen = new SolidColorBrush(Color.FromRgb(229, 231, 235));
            for (int g = 0; g <= 4; g++)
            {
                double gy = marginTop + plotH * g / 4.0;
                GraficoHoras.Children.Add(new Line
                {
                    X1 = marginLeft,
                    X2 = marginLeft + plotW,
                    Y1 = gy,
                    Y2 = gy,
                    Stroke = gridPen,
                    StrokeThickness = g == 4 ? 1 : 0.6
                });

                decimal val = maxY * (4 - g) / 4m;
                var yLbl = new TextBlock
                {
                    Text = val.ToString("C0", _cl),
                    FontSize = 8,
                    Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128))
                };
                Canvas.SetLeft(yLbl, 2);
                Canvas.SetTop(yLbl, gy - 7);
                GraficoHoras.Children.Add(yLbl);
            }

            double denom = Math.Max(1, datos.Count - 1);
            var puntos = new PointCollection();
            for (int i = 0; i < datos.Count; i++)
            {
                double px = marginLeft + plotW * i / denom;
                double ratio = maxY == 0 ? 0 : (double)(datos[i].Total / maxY);
                ratio = Math.Min(1, Math.Max(0, ratio));
                double py = marginTop + plotH - ratio * plotH;
                puntos.Add(new System.Windows.Point(px, py));
            }

            GraficoHoras.Children.Add(new Polyline
            {
                Points = puntos,
                Stroke = new SolidColorBrush(Color.FromRgb(29, 78, 216)),
                StrokeThickness = 2,
                Fill = Brushes.Transparent
            });

            for (int i = 0; i < datos.Count; i++)
            {
                double px = marginLeft + plotW * i / denom;
                double ratio = maxY == 0 ? 0 : (double)(datos[i].Total / maxY);
                ratio = Math.Min(1, Math.Max(0, ratio));
                double py = marginTop + plotH - ratio * plotH;

                var dot = new Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = Brushes.White,
                    Stroke = new SolidColorBrush(Color.FromRgb(29, 78, 216)),
                    StrokeThickness = 1.5
                };
                GraficoHoras.Children.Add(dot);
                Canvas.SetLeft(dot, px - 3);
                Canvas.SetTop(dot, py - 3);

                var lbl = new TextBlock
                {
                    Text = datos[i].Hora.ToString(),
                    FontSize = 9,
                    Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128))
                };
                Canvas.SetLeft(lbl, px - 6);
                Canvas.SetTop(lbl, marginTop + plotH + 3);
                GraficoHoras.Children.Add(lbl);
            }
        }

        private void CargarMetodos()
        {
            var hoy = DateTime.Today;
            string periodo = PeriodoTag();

            DateTime ini;
            DateTime fin = hoy.AddDays(1);

            switch (periodo)
            {
                case "7":
                    ini = hoy.AddDays(-6);
                    break;
                case "Mes":
                    (ini, fin) = RangoMesSeleccionado();
                    break;
                default:
                    ini = hoy.Date;
                    break;
            }

            var lista = _reporte.ObtenerVentasPorMetodoEnRango(ini, fin);
            GridMetodos.ItemsSource = lista;
            PanelMetodosVacio.Visibility = lista.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void DibujarTopProductos()
        {
            TopProductosPanel.Children.Clear();

            var hoy = DateTime.Today;
            string periodo = PeriodoTag();
            DateTime? desde = periodo switch
            {
                "7" => hoy.AddDays(-6),
                "Mes" => RangoMesSeleccionado().Inicio,
                _ => hoy.Date
            };

            var productos = _reporte.ObtenerProductosMasVendidos(8, desde).Take(5).ToList();

            PanelTopVacio.Visibility = productos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            int maxCant = productos.Count > 0 ? productos.Max(p => p.Cantidad) : 1;

            foreach (var p in productos)
            {
                var bloque = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };

                bloque.Children.Add(new TextBlock
                {
                    Text = p.Producto,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 4)
                });

                var barW = maxCant > 0 ? (double)p.Cantidad / maxCant * 200 : 0;
                bloque.Children.Add(new Border
                {
                    Height = 10,
                    Width = Math.Max(barW, 4),
                    Background = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                    CornerRadius = new CornerRadius(4),
                    HorizontalAlignment = HorizontalAlignment.Left
                });

                bloque.Children.Add(new TextBlock
                {
                    Text = $"{p.Cantidad} u. · {p.Total:C0}",
                    FontSize = 11,
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(0, 4, 0, 0)
                });

                TopProductosPanel.Children.Add(bloque);
            }
        }

        private void ActualizarEstadoNegocio()
        {
            var ini30 = DateTime.Today.AddDays(-29);
            var fin = DateTime.Today.AddDays(1);

            int diasCon = _reporte.ObtenerDiasConVentasEnRango(ini30, fin);
            decimal ventas30 = _reporte.ObtenerTotalVentasRango(ini30, fin);
            int trans30 = _reporte.ObtenerCantidadVentasRango(ini30, fin);
            int unidades30 = _reporte.ObtenerUnidadesVendidasRango(ini30, fin);

            TxtMetricDiasVentas.Text = diasCon.ToString("N0", _cl);
            TxtMetricVentas30.Text = ventas30.ToString("C0", _cl);
            TxtMetricTrans30.Text = trans30.ToString("N0", _cl);
            TxtMetricUnidades30.Text = $"{unidades30:N0} u.";

            var hoyIni = DateTime.Today;
            var hoyFin = DateTime.Today.AddDays(1);
            var ventasHoy = _reporte.ObtenerTotalVentasRango(hoyIni, hoyFin);

            if (ventasHoy == 0)
            {
                TxtEstadoAdvertencia.Text = "Aún no hay ventas registradas hoy en la base de datos.";
                TxtEstadoAdvertencia.Foreground = new SolidColorBrush(Color.FromRgb(234, 88, 12));
            }
            else
            {
                TxtEstadoAdvertencia.Text = $"Ventas de hoy: {ventasHoy.ToString("C0", _cl)}";
                TxtEstadoAdvertencia.Foreground = new SolidColorBrush(Color.FromRgb(22, 163, 74));
            }

            var top = _reporte.ObtenerProductosMasVendidos(1, DateTime.Today.AddDays(-30)).FirstOrDefault();
            if (top != null)
            {
                PanelLiderProducto.Visibility = Visibility.Visible;
                TxtLiderProducto.Text = $"Producto líder (30 días): {top.Producto} ({top.Cantidad} u.)";
            }
            else
            {
                PanelLiderProducto.Visibility = Visibility.Collapsed;
            }
        }

        private void KpiVentas_Click(object sender, MouseButtonEventArgs e)
        {
            var w = new VentasDelDiaView
            {
                Owner = Window.GetWindow(this)
            };
            w.ShowDialog();
        }

        private void KpiComparacion_Click(object sender, MouseButtonEventArgs e)
        {
            var hoy = DateTime.Today;
            string periodo = PeriodoTag();
            string msg;

            switch (periodo)
            {
                case "7":
                    {
                        var inicio = hoy.AddDays(-6);
                        var a = _reporte.ObtenerTotalVentasRango(inicio, hoy.AddDays(1));
                        var b = _reporte.ObtenerTotalVentasRango(inicio.AddDays(-7), inicio);
                        msg = $"Últimos 7 días: {a:C0}\n7 días previos: {b:C0}";
                        break;
                    }
                case "Mes":
                    {
                        var (inicio, fin) = RangoMesSeleccionado();
                        var inicioPrev = inicio.AddMonths(-1);
                        var a = _reporte.ObtenerTotalVentasRango(inicio, fin);
                        var b = _reporte.ObtenerTotalVentasRango(inicioPrev, inicio);
                        msg = $"Mes actual: {a:C0}\nMes anterior: {b:C0}";
                        break;
                    }
                default:
                    {
                        var a = _reporte.ObtenerTotalVentasRango(hoy, hoy.AddDays(1));
                        var b = _reporte.ObtenerTotalVentasRango(hoy.AddDays(-1), hoy);
                        msg = $"Hoy: {a:C0}\nAyer: {b:C0}";
                        break;
                    }
            }

            MessageBox.Show(msg, "Comparación de períodos", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void KpiVariacion_Click(object sender, MouseButtonEventArgs e)
        {
            var tag = PeriodoTag();
            MessageBox.Show(
                tag == "Hoy"
                    ? "Variación calculada entre ventas de hoy y ayer."
                    : tag == "7"
                        ? "Variación entre los últimos 7 días y los 7 días anteriores."
                        : "Variación entre el mes seleccionado completo y su mes anterior.",
                "Variación",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void KpiTicket_Click(object sender, MouseButtonEventArgs e)
        {
            GridMetodos.Focus();
            MessageBox.Show(
                "El ticket promedio es el total del período seleccionado dividido por la cantidad de ventas en ese rango.\n\nLa tabla de la derecha muestra el desglose por método de pago.",
                "Ticket promedio",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void ImprimirReporte_Click(object sender, RoutedEventArgs e)
        {
            var periodo = PeriodoTag();
            if (periodo == "Mes")
            {
                var (inicio, fin) = RangoMesSeleccionado();
                var nombreMes = _cl.DateTimeFormat.GetMonthName(inicio.Month);
                var titulo = $"{nombreMes} {inicio.Year}";
                ReportePdfService.GenerarReportePDF(periodoInicio: inicio, periodoFinExclusivo: fin, tituloPeriodo: titulo);
                return;
            }

            if (periodo == "7")
            {
                var fin = DateTime.Today.AddDays(1);
                var inicio = DateTime.Today.AddDays(-6);
                ReportePdfService.GenerarReportePDF(periodoInicio: inicio, periodoFinExclusivo: fin, tituloPeriodo: "ultimos 7 dias");
                return;
            }

            var inicioHoy = DateTime.Today;
            ReportePdfService.GenerarReportePDF(periodoInicio: inicioHoy, periodoFinExclusivo: inicioHoy.AddDays(1), tituloPeriodo: "hoy");
        }

        private sealed class MesItem
        {
            public int Numero { get; set; }
            public string Nombre { get; set; } = string.Empty;
        }
    }
}
