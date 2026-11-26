using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FarmStats.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAnimalQuantityEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NewQuantity",
                table: "AnimalHistory",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PreviousQuantity",
                table: "AnimalHistory",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QuantityDelta",
                table: "AnimalHistory",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QuantityEventType",
                table: "AnimalHistory",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_AnimalHistory_QuantityEvent",
                table: "AnimalHistory",
                sql: "(\n    \"EventType\" <> 4\n    AND \"QuantityEventType\" IS NULL\n    AND \"PreviousQuantity\" IS NULL\n    AND \"NewQuantity\" IS NULL\n    AND \"QuantityDelta\" IS NULL\n)\nOR\n(\n    \"EventType\" = 4\n    AND \"QuantityEventType\" IS NOT NULL\n    AND \"PreviousQuantity\" IS NOT NULL\n    AND \"NewQuantity\" IS NOT NULL\n    AND \"QuantityDelta\" IS NOT NULL\n    AND \"EffectiveDate\" IS NOT NULL\n    AND \"PreviousQuantity\" >= 0\n    AND \"NewQuantity\" >= 0\n    AND \"QuantityDelta\"::bigint =\n        \"NewQuantity\"::bigint - \"PreviousQuantity\"::bigint\n    AND\n    (\n        (\"QuantityEventType\" = 0 AND \"QuantityDelta\" > 0)\n        OR\n        (\"QuantityEventType\" IN (1, 2) AND \"QuantityDelta\" < 0)\n        OR\n        (\n            \"QuantityEventType\" = 3\n            AND \"QuantityDelta\" <> 0\n            AND \"Notes\" IS NOT NULL\n            AND length(btrim(\"Notes\")) > 0\n        )\n    )\n)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AnimalHistory_QuantityEvent",
                table: "AnimalHistory");

            migrationBuilder.DropColumn(
                name: "NewQuantity",
                table: "AnimalHistory");

            migrationBuilder.DropColumn(
                name: "PreviousQuantity",
                table: "AnimalHistory");

            migrationBuilder.DropColumn(
                name: "QuantityDelta",
                table: "AnimalHistory");

            migrationBuilder.DropColumn(
                name: "QuantityEventType",
                table: "AnimalHistory");
        }
    }
}
