using System.IO;
using System.Text.Json;

namespace GrunflexPOS2.Data
{
    public class AppConfig
    {
        public string Servidor { get; set; } = "";
        public int Puerto { get; set; }
        public string BaseDatos { get; set; } = "";
        public string Usuario { get; set; } = "";
        public string Password { get; set; } = "";
        public string CajaId { get; set; } = "";

        public static AppConfig Cargar()
        {
            var json = File.ReadAllText("appsettings.local.json");
            return JsonSerializer.Deserialize<AppConfig>(json)!;
        }

        public void Guardar()
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText("appsettings.local.json", json);
        }
    }
}