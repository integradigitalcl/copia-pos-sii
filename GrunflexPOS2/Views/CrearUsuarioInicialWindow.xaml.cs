using System;
using System.Linq;
using System.Windows;
using Grunflex.Licensing.Security;
using GrunflexPOS2.Data;
using GrunflexPOS2.Models.Entities;
using GrunflexPOS2.Services;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS2.Views;

public partial class CrearUsuarioInicialWindow : Window
{
    public CrearUsuarioInicialWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppConfig.Cargar().EsCajaAdicional)
            GrunflexDataDirectoryAcl.TryRepairCommerceDatabase();
        TxtUsuario.Focus();
    }

    private void BtnCrear_Click(object sender, RoutedEventArgs e)
    {
        TxtError.Visibility = Visibility.Collapsed;

        var nombre = TxtNombre.Text.Trim();
        var usuario = TxtUsuario.Text.Trim();
        var pass = TxtPassword.Password;
        var pass2 = TxtPasswordConfirm.Password;

        if (string.IsNullOrWhiteSpace(usuario))
        {
            MostrarError("Ingrese un nombre de usuario.");
            return;
        }

        if (usuario.Contains(' ', StringComparison.Ordinal))
        {
            MostrarError("El usuario no debe contener espacios.");
            return;
        }

        if (string.IsNullOrEmpty(pass) || pass.Length < 4)
        {
            MostrarError("La contraseña debe tener al menos 4 caracteres.");
            return;
        }

        if (!string.Equals(pass, pass2, StringComparison.Ordinal))
        {
            MostrarError("Las contraseñas no coinciden.");
            return;
        }

        if (App.DbContext.Usuarios.Any(u => u.Username == usuario))
        {
            MostrarError("Ese nombre de usuario ya existe.");
            return;
        }

        var entity = new Usuario
        {
            Id = Guid.NewGuid(),
            Username = usuario,
            Password = PasswordHasher.Hash(pass),
            Nombre = string.IsNullOrWhiteSpace(nombre) ? usuario : nombre,
            Rol = "Admin"
        };

        try
        {
            App.DbContext.Usuarios.Add(entity);
            App.DbContext.SaveChanges();
        }
        catch (Exception ex) when (IsSqliteReadonly(ex))
        {
            PosDiagnostics.Log("CrearUsuarioInicial: SQLite readonly", ex);
            if (GrunflexDataDirectoryAcl.TryRepairCommerceDatabase())
            {
                try
                {
                    App.DbContext.Usuarios.Add(entity);
                    App.DbContext.SaveChanges();
                }
                catch (Exception ex2)
                {
                    PosDiagnostics.Log("CrearUsuarioInicial: retry failed", ex2);
                    MostrarError(MensajeReadonly());
                    return;
                }
            }
            else
            {
                MostrarError(MensajeReadonly());
                return;
            }
        }

        DialogResult = true;
        Close();
    }

    private static bool IsSqliteReadonly(Exception ex)
    {
        for (var cur = ex; cur != null; cur = cur.InnerException)
        {
            var msg = cur.Message;
            if (msg.Contains("readonly database", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("read-only database", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("SQLite Error 8", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string MensajeReadonly()
    {
        if (PosEdgeRoleProbe.IsPosEdgeTerminal() || AppConfig.Cargar().EsCajaAdicional)
        {
            return
                "Esta PC está configurada como caja adicional: no debe crear usuarios aquí.\n\n" +
                "Use el mismo usuario y contraseña de la caja principal.\n\n" +
                "Si ve este mensaje tras instalar como terminal, reinstale eligiendo «Caja adicional» " +
                "o ejecute «Reparar sistema» desde el menú de Grunflex POS (como administrador).";
        }

        return
            "No se puede escribir la base de datos en:\n" +
            LocalDatabasePaths.DataDirectory + "\n\n" +
            "En la caja principal la API crea el archivo en ProgramData; su usuario debe tener permiso de escritura.\n\n" +
            "Cierre el POS, abra el menú Inicio → Grunflex POS → «Reparar sistema» (clic derecho → Ejecutar como administrador), " +
            "y vuelva a crear el administrador.";
    }

    private void MostrarError(string mensaje)
    {
        TxtError.Text = mensaje;
        TxtError.Visibility = Visibility.Visible;
    }
}
