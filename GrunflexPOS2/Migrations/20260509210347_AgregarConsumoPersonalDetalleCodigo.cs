using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GrunflexPOS2.Migrations
{
    /// <inheritdoc />
    public partial class AgregarConsumoPersonalDetalleCodigo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EsConsumoPersonal",
                table: "Ventas",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CodigoBarras",
                table: "DetalleVentas",
                type: "TEXT",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EsConsumoPersonal",
                table: "Ventas");

            migrationBuilder.DropColumn(
                name: "CodigoBarras",
                table: "DetalleVentas");
        }
    }
}
