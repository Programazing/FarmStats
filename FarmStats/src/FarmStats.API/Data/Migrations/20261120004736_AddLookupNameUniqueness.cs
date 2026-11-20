using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FarmStats.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLookupNameUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Breeds_SpeciesId",
                table: "Breeds");

            migrationBuilder.AddColumn<string>(
                name: "NormalizedName",
                table: "Species",
                type: "text",
                nullable: true,
                computedColumnSql: "upper(btrim(\"Name\"))",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedName",
                table: "Breeds",
                type: "text",
                nullable: true,
                computedColumnSql: "upper(btrim(\"Name\"))",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedName",
                table: "Areas",
                type: "text",
                nullable: true,
                computedColumnSql: "upper(btrim(\"Name\"))",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "UX_Species_NormalizedName",
                table: "Species",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Breeds_SpeciesId_NormalizedName",
                table: "Breeds",
                columns: new[] { "SpeciesId", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Areas_NormalizedName",
                table: "Areas",
                column: "NormalizedName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Species_NormalizedName",
                table: "Species");

            migrationBuilder.DropIndex(
                name: "UX_Breeds_SpeciesId_NormalizedName",
                table: "Breeds");

            migrationBuilder.DropIndex(
                name: "UX_Areas_NormalizedName",
                table: "Areas");

            migrationBuilder.DropColumn(
                name: "NormalizedName",
                table: "Species");

            migrationBuilder.DropColumn(
                name: "NormalizedName",
                table: "Breeds");

            migrationBuilder.DropColumn(
                name: "NormalizedName",
                table: "Areas");

            migrationBuilder.CreateIndex(
                name: "IX_Breeds_SpeciesId",
                table: "Breeds",
                column: "SpeciesId");
        }
    }
}
