using System.Globalization;
using ClosedXML.Excel;
using GrunflexPOS2.Data;
using GrunflexPOS2.Models.Entities;

namespace GrunflexPOS2.Services;

/// <summary>Importa inventario desde .xlsx (Código, Producto, P. Costo, P. Venta, Existencia, etc.). Catálogo vacío: crea productos; con datos: suma stock y opcionalmente actualiza nombre/precio/costo.</summary>
public static class InventarioExcelImportService
{
    public sealed class ResultadoImportacion
    {
        public int FilasProcesadas { get; init; }
        public int ProductosActualizados { get; init; }
        public IReadOnlyList<string> Errores { get; init; } = Array.Empty<string>();
    }

    private sealed class AcumImport
    {
        public int CantidadAcum;
        public string? Nombre;
        public decimal? Costo;
        public decimal? Precio;
        public decimal? PrecioMayoreo;
        public int? InvMinimo;
        public int? InvMaximo;
        public string? Departamento;
        public string? TipoVenta;
    }

    private sealed class ColumnasImport
    {
        public int ColCodigo = 1;
        public int? ColCantidadFija;
        public int? ColNombre;
        public int? ColCosto;
        public int? ColPrecio;
        public int? ColMayoreo;
        public int? ColDepto;
        public int? ColInvMin;
        public int? ColInvMax;
        public int? ColTipoVenta;

        public bool EsColumnaNoStockHeuristic(int c) =>
            c == ColCosto || c == ColPrecio || c == ColMayoreo || c == ColNombre || c == ColDepto ||
            c == ColTipoVenta || c == ColInvMin || c == ColInvMax;
    }

    private const int MaxColBusqueda = 40;
    private const int MaxCantidadAbs = 50_000_000;

    public static ResultadoImportacion ImportarAjustesDesdeExcel(GrunflexDbContext db, string rutaArchivo)
    {
        var errores = new List<string>();
        var agregados = new Dictionary<string, AcumImport>(StringComparer.OrdinalIgnoreCase);

        using var libro = new XLWorkbook(rutaArchivo);
        var hoja = libro.Worksheets.FirstOrDefault();
        if (hoja == null)
        {
            return new ResultadoImportacion
            {
                Errores = new[] { "El archivo no tiene hojas." }
            };
        }

        int ultimaFila = hoja.LastRowUsed()?.RowNumber() ?? 0;
        int ultimaCol = Math.Min(hoja.LastColumnUsed()?.ColumnNumber() ?? 1, MaxColBusqueda);
        if (ultimaFila < 1)
        {
            return new ResultadoImportacion
            {
                Errores = new[] { "No hay filas en la hoja." }
            };
        }

        bool encabezadoEnFila1 = FilaPareceEncabezado(hoja, 1, ultimaCol);
        int filaDatos = encabezadoEnFila1 ? 2 : 1;

        var cols = DetectarColumnas(hoja, encabezadoEnFila1 ? 1 : 0, ultimaCol);

        if (ultimaFila < filaDatos)
        {
            return new ResultadoImportacion
            {
                Errores = new[] { "No hay filas de datos debajo del encabezado." }
            };
        }

        int filasLeidas = 0;
        int filasSinCantidad = 0;
        for (int r = filaDatos; r <= ultimaFila; r++)
        {
            string codigo = CeldaComoTexto(hoja.Cell(r, cols.ColCodigo)).Trim();
            if (string.IsNullOrWhiteSpace(codigo))
                continue;

            filasLeidas++;

            int? cantidad = null;
            if (cols.ColCantidadFija is int colFija)
            {
                if (TryParseCantidad(CeldaComoTexto(hoja.Cell(r, colFija)), out int q))
                    cantidad = q;
            }
            else
            {
                for (int col = cols.ColCodigo + 1; col <= ultimaCol; col++)
                {
                    if (cols.EsColumnaNoStockHeuristic(col))
                        continue;
                    if (TryParseCantidad(CeldaComoTexto(hoja.Cell(r, col)), out int q) && PareceCantidadStock(q))
                    {
                        cantidad = q;
                        break;
                    }
                }
            }

            if (!cantidad.HasValue)
            {
                filasSinCantidad++;
                if (errores.Count < 30)
                    errores.Add($"Fila {r}: sin cantidad numérica a la derecha del código (columna {LetraColumna(cols.ColCodigo)}).");
                continue;
            }

            string? nombreFila = null;
            if (cols.ColNombre is int colNom)
            {
                string nom = CeldaComoTexto(hoja.Cell(r, colNom)).Trim();
                if (!string.IsNullOrWhiteSpace(nom))
                    nombreFila = nom;
            }

            decimal? costoFila = LeerDecimalCelda(hoja, r, cols.ColCosto);
            decimal? precioFila = LeerDecimalCelda(hoja, r, cols.ColPrecio);
            decimal? mayFila = LeerDecimalCelda(hoja, r, cols.ColMayoreo);
            int? invMinF = LeerIntCelda(hoja, r, cols.ColInvMin);
            int? invMaxF = LeerIntCelda(hoja, r, cols.ColInvMax);
            string? deptoF = cols.ColDepto is int cd ? CeldaComoTexto(hoja.Cell(r, cd)).Trim() : null;
            string? tipoF = cols.ColTipoVenta is int ct ? CeldaComoTexto(hoja.Cell(r, ct)).Trim() : null;

            if (!agregados.TryGetValue(codigo, out var acum))
            {
                acum = new AcumImport();
                agregados[codigo] = acum;
            }

            acum.CantidadAcum += cantidad.Value;
            if (string.IsNullOrWhiteSpace(acum.Nombre) && !string.IsNullOrWhiteSpace(nombreFila))
                acum.Nombre = nombreFila.Trim();
            if (costoFila.HasValue)
                acum.Costo = costoFila;
            if (precioFila.HasValue)
                acum.Precio = precioFila;
            if (mayFila.HasValue)
                acum.PrecioMayoreo = mayFila;
            if (invMinF.HasValue)
                acum.InvMinimo = invMinF;
            if (invMaxF.HasValue)
                acum.InvMaximo = invMaxF;
            if (!string.IsNullOrWhiteSpace(deptoF))
                acum.Departamento = deptoF;
            if (!string.IsNullOrWhiteSpace(tipoF))
                acum.TipoVenta = tipoF;
        }

        if (agregados.Count == 0)
        {
            var msg = filasLeidas == 0
                ? "No se encontraron códigos en la columna de código detectada."
                : $"Se leyeron {filasLeidas} filas con código, pero ninguna tenía cantidad válida (p. ej. columna «Existencia»).";
            var lista = new List<string> { msg };
            lista.AddRange(errores.Take(20));
            return new ResultadoImportacion
            {
                FilasProcesadas = filasLeidas,
                Errores = lista
            };
        }

        bool catalogoVacio = !db.Productos.Any();

        int actualizados = 0;
        if (catalogoVacio)
        {
            try
            {
                foreach (var kv in agregados)
                {
                    string cod = kv.Key.Trim();
                    if (string.IsNullOrWhiteSpace(cod))
                        continue;
                    string codDb = cod.Length > 50 ? cod[..50] : cod;
                    var a = kv.Value;

                    string nombre = !string.IsNullOrWhiteSpace(a.Nombre)
                        ? a.Nombre.Trim()
                        : $"Producto {codDb}";
                    if (nombre.Length > 2000)
                        nombre = nombre[..2000];

                    db.Productos.Add(new Producto
                    {
                        CodigoBarras = codDb,
                        Nombre = nombre,
                        Stock = a.CantidadAcum,
                        Costo = a.Costo ?? 0,
                        Precio = a.Precio ?? 0,
                        PrecioMayoreo = a.PrecioMayoreo ?? 0,
                        InvMinimo = a.InvMinimo ?? 0,
                        InvMaximo = a.InvMaximo ?? 0,
                        Departamento = (a.Departamento ?? "").Length > 120 ? (a.Departamento ?? "")[..120] : (a.Departamento ?? ""),
                        TipoVenta = (a.TipoVenta ?? "").Length > 50 ? (a.TipoVenta ?? "")[..50] : (a.TipoVenta ?? "")
                    });
                    actualizados++;
                }

                db.SaveChanges();
                errores.Insert(0,
                    $"Catálogo vacío: se crearon {actualizados} productos. Se aplicó Existencia como stock inicial. " +
                    "Si el Excel trae «P. Costo» / «P. Venta» / «Producto», también se importaron.");
            }
            catch (Exception ex)
            {
                return new ResultadoImportacion
                {
                    FilasProcesadas = filasLeidas,
                    Errores = new[]
                    {
                        "No se pudieron crear los productos (¿códigos duplicados?). Detalle: " + ex.Message
                    }
                };
            }
        }
        else
        {
            var todosProductos = db.Productos.ToList();
            var codigoAProducto = ConstruirIndiceCodigos(todosProductos);
            var porId = todosProductos.ToDictionary(p => p.Id);

            int noEncontrados = 0;
            foreach (var kv in agregados)
            {
                var prod = ResolverProducto(kv.Key, codigoAProducto, porId);
                if (prod == null)
                {
                    noEncontrados++;
                    if (errores.Count < 35)
                        errores.Add($"Código no encontrado: «{kv.Key}».");
                    continue;
                }

                var a = kv.Value;
                prod.Stock += a.CantidadAcum;
                if (!string.IsNullOrWhiteSpace(a.Nombre))
                    prod.Nombre = a.Nombre.Trim();
                if (a.Costo.HasValue)
                    prod.Costo = a.Costo.Value;
                if (a.Precio.HasValue)
                    prod.Precio = a.Precio.Value;
                if (a.PrecioMayoreo.HasValue)
                    prod.PrecioMayoreo = a.PrecioMayoreo.Value;
                if (a.InvMinimo.HasValue)
                    prod.InvMinimo = a.InvMinimo.Value;
                if (a.InvMaximo.HasValue)
                    prod.InvMaximo = a.InvMaximo.Value;
                if (!string.IsNullOrWhiteSpace(a.Departamento))
                    prod.Departamento = a.Departamento.Length > 120 ? a.Departamento[..120] : a.Departamento;
                if (!string.IsNullOrWhiteSpace(a.TipoVenta))
                    prod.TipoVenta = a.TipoVenta.Length > 50 ? a.TipoVenta[..50] : a.TipoVenta;
                actualizados++;
            }

            db.SaveChanges();

            if (noEncontrados > 0)
            {
                if (actualizados == 0)
                {
                    errores.Insert(0,
                        "Ningún código del Excel coincide con los productos de esta base (campo «código de barras» o Id). " +
                        "Revise que el catálogo use los mismos códigos que el archivo, o exporte códigos desde F3-Productos.");
                }

                if (noEncontrados > 35)
                    errores.Add($"… y {noEncontrados - 35} códigos más sin coincidencia.");
            }
        }

        if (filasSinCantidad > 0 && errores.Count < 200)
            errores.Add($"Filas con código pero sin cantidad detectada: {filasSinCantidad}.");

        return new ResultadoImportacion
        {
            FilasProcesadas = filasLeidas,
            ProductosActualizados = actualizados,
            Errores = errores
        };
    }

    private static decimal? LeerDecimalCelda(IXLWorksheet hoja, int fila, int? col)
    {
        if (col == null)
            return null;
        string t = CeldaComoTexto(hoja.Cell(fila, col.Value));
        return TryParseMoney(t, out decimal d) ? d : null;
    }

    private static int? LeerIntCelda(IXLWorksheet hoja, int fila, int? col)
    {
        if (col == null)
            return null;
        string t = CeldaComoTexto(hoja.Cell(fila, col.Value));
        return TryParseCantidad(t, out int n) ? n : null;
    }

    private static bool TryParseMoney(string? s, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s))
            return false;
        s = s.Trim().Replace("$", "").Replace("€", "").Replace(" ", "").Replace("\u00A0", "");
        if (decimal.TryParse(s, NumberStyles.Number, new CultureInfo("es-CL"), out value))
            return true;
        if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
            return true;
        string sinPuntosMiles = s.Replace(".", "");
        return decimal.TryParse(sinPuntosMiles, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    private static bool EsSoloDigitos(string s) => s.Length > 0 && s.All(char.IsDigit);

    private static HashSet<string> ClavesBusquedaCodigo(string codigo)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return;
            var t = s.Trim();
            if (t.Length > 0)
                set.Add(t);
        }

        Add(codigo);
        string compact = codigo.Trim().Replace(" ", "").Replace("\u00A0", "");
        if (compact.Length > 0 && EsSoloDigitos(compact))
        {
            string d = compact;
            string nz = d.TrimStart('0');
            if (nz.Length == 0)
                nz = "0";
            Add(d);
            Add(nz);
            foreach (int len in new[] { 8, 12, 13, 14 })
            {
                if (d.Length <= len)
                    Add(d.PadLeft(len, '0'));
                if (nz.Length <= len)
                    Add(nz.PadLeft(len, '0'));
            }
        }

        return set;
    }

    private static Dictionary<string, Producto> ConstruirIndiceCodigos(List<Producto> productos)
    {
        var map = new Dictionary<string, Producto>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in productos)
        {
            foreach (var clave in ClavesBusquedaCodigo(p.CodigoBarras ?? ""))
            {
                if (!map.ContainsKey(clave))
                    map[clave] = p;
            }
        }

        return map;
    }

    private static Producto? ResolverProducto(
        string codigoExcel,
        Dictionary<string, Producto> codigoAProducto,
        IReadOnlyDictionary<int, Producto> porId)
    {
        foreach (var k in ClavesBusquedaCodigo(codigoExcel))
        {
            if (codigoAProducto.TryGetValue(k, out var p))
                return p;
        }

        string tr = codigoExcel.Trim();
        if (int.TryParse(tr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) && id > 0 &&
            porId.TryGetValue(id, out var porIdProd))
            return porIdProd;

        return null;
    }

    private static string LetraColumna(int col)
    {
        string s = "";
        while (col > 0)
        {
            col--;
            s = (char)('A' + col % 26) + s;
            col /= 26;
        }

        return s;
    }

    private static ColumnasImport DetectarColumnas(IXLWorksheet hoja, int filaEncabezado, int ultimaCol)
    {
        var z = new ColumnasImport();
        if (filaEncabezado < 1)
            return z;

        int? cod = null;
        int? cant = null;
        int? nom = null;
        int? cst = null;
        int? prv = null;
        int? may = null;
        int? dep = null;
        int? imn = null;
        int? imx = null;
        int? tvt = null;
        for (int c = 1; c <= ultimaCol; c++)
        {
            var norm = NormalizarEncabezado(CeldaComoTexto(hoja.Cell(filaEncabezado, c)));
            if (string.IsNullOrEmpty(norm))
                continue;
            if (cod == null && EsEncabezadoCodigo(norm))
                cod = c;
            if (tvt == null && EsEncabezadoTipoVenta(norm))
                tvt = c;
            if (cant == null && EsEncabezadoCantidad(norm))
                cant = c;
            if (nom == null && EsEncabezadoNombre(norm))
                nom = c;
            if (cst == null && EsEncabezadoCosto(norm))
                cst = c;
            if (prv == null && EsEncabezadoPrecioVenta(norm))
                prv = c;
            if (may == null && EsEncabezadoMayoreo(norm))
                may = c;
            if (dep == null && EsEncabezadoDepartamento(norm))
                dep = c;
            if (imn == null && EsEncabezadoInvMinimo(norm))
                imn = c;
            if (imx == null && EsEncabezadoInvMaximo(norm))
                imx = c;
        }

        if (cod.HasValue)
            z.ColCodigo = cod.Value;
        if (cant.HasValue)
            z.ColCantidadFija = cant.Value;
        if (nom.HasValue)
            z.ColNombre = nom.Value;
        if (cst.HasValue)
            z.ColCosto = cst.Value;
        if (prv.HasValue)
            z.ColPrecio = prv.Value;
        if (may.HasValue)
            z.ColMayoreo = may.Value;
        if (dep.HasValue)
            z.ColDepto = dep.Value;
        if (imn.HasValue)
            z.ColInvMin = imn.Value;
        if (imx.HasValue)
            z.ColInvMax = imx.Value;
        if (tvt.HasValue)
            z.ColTipoVenta = tvt.Value;
        return z;
    }

    private static bool FilaPareceEncabezado(IXLWorksheet hoja, int fila, int ultimaCol)
    {
        for (int c = 1; c <= ultimaCol; c++)
        {
            var norm = NormalizarEncabezado(CeldaComoTexto(hoja.Cell(fila, c)));
            if (EsEncabezadoCodigo(norm) || EsEncabezadoCantidad(norm) || EsEncabezadoNombre(norm) ||
                EsEncabezadoCosto(norm) || EsEncabezadoPrecioVenta(norm) || EsEncabezadoMayoreo(norm) ||
                EsEncabezadoDepartamento(norm) || EsEncabezadoInvMinimo(norm) || EsEncabezadoInvMaximo(norm) ||
                EsEncabezadoTipoVenta(norm))
                return true;
        }

        return DetectarPrimeraFilaDatosLegacy(hoja) == 2;
    }

    private static int DetectarPrimeraFilaDatosLegacy(IXLWorksheet hoja)
    {
        var a1 = CeldaComoTexto(hoja.Cell(1, 1)).Trim();
        if (string.IsNullOrWhiteSpace(a1))
            return 1;
        if (a1.StartsWith("cod", StringComparison.OrdinalIgnoreCase))
            return 2;
        if (a1.StartsWith("cód", StringComparison.OrdinalIgnoreCase))
            return 2;
        if (a1.Contains("barras", StringComparison.OrdinalIgnoreCase))
            return 2;
        if (a1.Equals("sku", StringComparison.OrdinalIgnoreCase))
            return 2;
        if (a1.Equals("código", StringComparison.OrdinalIgnoreCase))
            return 2;
        if (a1.Equals("codigo", StringComparison.OrdinalIgnoreCase))
            return 2;
        return 1;
    }

    private static string NormalizarEncabezado(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return string.Empty;
        s = s.Trim().ToLowerInvariant();
        return s
            .Replace("ó", "o", StringComparison.Ordinal)
            .Replace("á", "a", StringComparison.Ordinal)
            .Replace("é", "e", StringComparison.Ordinal)
            .Replace("í", "i", StringComparison.Ordinal)
            .Replace("ú", "u", StringComparison.Ordinal)
            .Replace("ñ", "n", StringComparison.Ordinal);
    }

    private static bool EsEncabezadoCodigo(string norm)
    {
        if (string.IsNullOrEmpty(norm))
            return false;
        if (norm.Contains("barras"))
            return true;
        if (norm == "sku" || norm.Contains("ean") || norm.Contains("upc"))
            return true;
        if (norm.Contains("codigo") || norm.Contains("clave") || norm.Contains("referencia"))
            return true;
        return false;
    }

    private static bool EsEncabezadoCantidad(string norm)
    {
        if (string.IsNullOrEmpty(norm))
            return false;
        if (norm.Contains("cant"))
            return true;
        if (norm.Contains("stock"))
            return true;
        if (norm.Contains("invent"))
            return true;
        if (norm.Contains("exist"))
            return true;
        if (norm is "qty" || norm.StartsWith("qty ", StringComparison.Ordinal))
            return true;
        if (norm.Contains("saldo"))
            return true;
        if (norm.Contains("ajuste"))
            return true;
        return false;
    }

    private static bool EsEncabezadoNombre(string norm)
    {
        if (string.IsNullOrEmpty(norm))
            return false;
        if (norm.Contains("nombre"))
            return true;
        if (norm.Contains("descrip"))
            return true;
        if (norm.Contains("producto") && !norm.Contains("codigo"))
            return true;
        if (norm.Contains("articulo"))
            return true;
        if (norm is "item" || norm.StartsWith("item ", StringComparison.Ordinal))
            return true;
        if (norm.Contains("detalle"))
            return true;
        return false;
    }

    private static bool EsEncabezadoCosto(string norm)
    {
        if (string.IsNullOrEmpty(norm))
            return false;
        if (norm.Contains("mayoreo") || norm.Contains("mayor"))
            return false;
        if (norm.Contains("venta"))
            return false;
        return norm.Contains("costo") || norm.Contains("coste");
    }

    private static bool EsEncabezadoTipoVenta(string norm) =>
        !string.IsNullOrEmpty(norm) && norm.Contains("tipo") && norm.Contains("venta");

    private static bool EsEncabezadoPrecioVenta(string norm)
    {
        if (string.IsNullOrEmpty(norm) || EsEncabezadoTipoVenta(norm))
            return false;
        if (norm.Contains("mayoreo") || norm.Contains("mayor"))
            return false;
        if (norm.Contains("costo") || norm.Contains("coste"))
            return false;
        if (norm.Contains("venta"))
            return true;
        if (norm.Contains("pvp") || norm.Contains("publico"))
            return true;
        return norm == "precio";
    }

    private static bool EsEncabezadoMayoreo(string norm) =>
        !string.IsNullOrEmpty(norm) && (norm.Contains("mayoreo") || norm.Contains("mayorista"));

    private static bool EsEncabezadoDepartamento(string norm) =>
        !string.IsNullOrEmpty(norm) &&
        (norm.Contains("depart") || norm.Contains("rubro") || norm.Contains("familia") || norm.Contains("seccion"));

    private static bool EsEncabezadoInvMinimo(string norm) =>
        !string.IsNullOrEmpty(norm) &&
        ((norm.Contains("min") && (norm.Contains("inv") || norm.Contains("invent"))) || norm.Contains("minimo"));

    private static bool EsEncabezadoInvMaximo(string norm) =>
        !string.IsNullOrEmpty(norm) &&
        ((norm.Contains("max") && (norm.Contains("inv") || norm.Contains("invent"))) || norm.Contains("maximo"));

    private static bool PareceCantidadStock(int v) => Math.Abs(v) <= MaxCantidadAbs;

    private static bool TryParseCantidad(string s, out int cantidad)
    {
        cantidad = 0;
        if (string.IsNullOrWhiteSpace(s))
            return false;
        s = s.Trim().Replace('\u00A0', ' ');
        var styles = NumberStyles.Integer | NumberStyles.AllowLeadingSign | NumberStyles.AllowThousands;

        if (int.TryParse(s, styles, CultureInfo.InvariantCulture, out cantidad))
            return true;
        if (int.TryParse(s, styles, new CultureInfo("es-CL"), out cantidad))
            return true;

        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ||
            double.TryParse(s, NumberStyles.Float, new CultureInfo("es-CL"), out d))
        {
            cantidad = (int)Math.Round(d);
            return true;
        }

        return false;
    }

    private static string CeldaComoTexto(IXLCell c)
    {
        var v = c.Value;
        if (v.IsBlank)
            return string.Empty;
        if (v.IsError)
            return string.Empty;
        if (v.IsBoolean)
            return v.GetBoolean() ? "1" : "0";
        if (v.IsNumber)
        {
            double d = v.GetNumber();
            if (Math.Abs(d - Math.Round(d)) < 1e-9)
                return ((long)Math.Round(d)).ToString(CultureInfo.InvariantCulture);
            return d.ToString(CultureInfo.InvariantCulture);
        }

        if (v.IsDateTime)
            return string.Empty;
        if (v.IsTimeSpan)
            return string.Empty;
        if (v.IsText)
            return v.GetText().Trim();

        return v.ToString().Trim();
    }
}
