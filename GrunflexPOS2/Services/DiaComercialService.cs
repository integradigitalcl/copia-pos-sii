public class DiaComercialService
{
    private readonly TimeSpan _horaInicio;

    public DiaComercialService(TimeSpan horaInicio)
    {
        _horaInicio = horaInicio;
    }

    public DateTime ObtenerFechaComercialActual()
    {
        var ahora = DateTime.Now;

        if (ahora.TimeOfDay < _horaInicio)
            return ahora.Date.AddDays(-1);

        return ahora.Date;
    }

    public (DateTime inicio, DateTime fin) ObtenerRangoDiaComercial(DateTime fechaComercial)
    {
        var inicio = fechaComercial.Date.Add(_horaInicio);
        var fin = inicio.AddDays(1);

        return (inicio, fin);
    }
}