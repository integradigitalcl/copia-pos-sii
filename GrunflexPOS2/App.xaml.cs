using System;
using System.Linq;
using System.Windows;
using PdfSharp.Fonts;
using GrunflexPOS2.Services;
using Microsoft.EntityFrameworkCore;
using GrunflexPOS2.Data;
using GrunflexPOS2.Models.Entities;

namespace GrunflexPOS2
{
    public partial class App : Application
    {
        public static VentaService VentaService { get; private set; } = null!;
        public static GrunflexDbContext DbContext { get; private set; } = null!;

        public App()
        {
            GlobalFontSettings.FontResolver = new CustomFontResolver();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            var options = new DbContextOptionsBuilder<GrunflexDbContext>()
                .UseNpgsql("Host=192.168.100.17;Port=5432;Database=grunflexpos2;Username=postgres;Password=2106")
                .Options;

            DbContext = new GrunflexDbContext(options);

            try
            {
                DbContext.Database.CanConnect();

                // 🔥 INICIALIZAR EMPRESA Y CAJA SI NO EXISTEN
                InicializarSistema();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error conectando a PostgreSQL:\n" + ex.Message);
            }

            VentaService = new VentaService();

            base.OnStartup(e);
        }

        private void InicializarSistema()
        {
            if (!DbContext.Empresas.Any())
            {
                var empresa = new Empresa
                {
                    Nombre = "Empresa Principal"
                };

                DbContext.Empresas.Add(empresa);
                DbContext.SaveChanges();

                var caja = new Caja
                {
                    Nombre = "Caja Principal",
                    EmpresaId = empresa.Id
                };

                DbContext.Cajas.Add(caja);
                DbContext.SaveChanges();
            }
        }
    }
}