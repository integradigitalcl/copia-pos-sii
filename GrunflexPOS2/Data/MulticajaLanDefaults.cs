namespace GrunflexPOS2.Data
{
    /// <summary>
    /// Credenciales fijas del recurso SMB multicaja (misma LAN, modelo tipo Eleventa).
    /// Sin caracteres especiales (#, etc.) para evitar errores en shells y en JSON.
    /// </summary>
    public static class MulticajaLanDefaults
    {
        public const string ShareUser = "grunflexshare";

        /// <summary>Debe coincidir con el usuario creado por el instalador en la caja principal.</summary>
        public const string SharePassword = "GrunflexLan2025SMB";
    }
}
