using System;

namespace GrunflexPOS.API.Models;

/// <summary>
/// Una "caja" (terminal POS) registrada contra la API local.
/// Se cuentan los slots activos contra la licencia (Multicaja.NumberOfBoxes).
/// </summary>
public class TerminalRegistration
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Huella única de la máquina (mac+volumeId hash). Idempotente.</summary>
    public string MachineFingerprint { get; set; } = string.Empty;

    /// <summary>Nombre legible (DNS/hostname).</summary>
    public string MachineName { get; set; } = string.Empty;

    /// <summary>Versión del POS reportada por el cliente.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Token de activación bajo el que se asoció esta terminal.</summary>
    public string ActivationId { get; set; } = string.Empty;

    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastHeartbeatUtc { get; set; } = DateTime.UtcNow;

    /// <summary>True = ocupa un slot. False = desocupado (uninstall, expulsado).</summary>
    public bool Active { get; set; } = true;

    public string? IpAddress { get; set; }

    /// <summary>Identidad persistente de instalación (GUID generado en el POS, independiente del hardware).</summary>
    public Guid? InstallationId { get; set; }

    /// <summary>Caja asignada a esta terminal.</summary>
    public Guid? CajaId { get; set; }

    /// <summary>Sucursal opcional.</summary>
    public Guid? BranchId { get; set; }

    /// <summary>SHA-256 hex del token de terminal (se envía en claro solo en registro inicial LAN).</summary>
    public string? TerminalTokenHash { get; set; }

    /// <summary>Nombre administrable (no depende de hostname).</summary>
    public string? DisplayName { get; set; }

    public DateTime? DeactivatedAtUtc { get; set; }
    public string? DeactivatedReason { get; set; }
}
