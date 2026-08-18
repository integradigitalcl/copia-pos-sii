namespace GrunflexPOS2.Services.Connectivity;

/// <summary>Estado consolidado de conectividad con el servidor (multicaja o local).</summary>
public enum ConnectivityState
{
    /// <summary>Aún no se ha probado.</summary>
    Unknown,
    /// <summary>Server alcanzable y respondiendo dentro de SLA.</summary>
    Online,
    /// <summary>Respondiendo pero lento (>1s).</summary>
    Degraded,
    /// <summary>No responde.</summary>
    Offline
}
