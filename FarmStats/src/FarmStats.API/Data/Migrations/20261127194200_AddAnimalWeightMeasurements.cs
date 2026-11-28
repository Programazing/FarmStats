using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FarmStats.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAnimalWeightMeasurements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AnimalWeightMeasurements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AnimalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Value = table.Column<decimal>(type: "numeric(10,3)", precision: 10, scale: 3, nullable: false),
                    Unit = table.Column<int>(type: "integer", nullable: false),
                    MeasurementDate = table.Column<DateOnly>(type: "date", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnimalWeightMeasurements", x => x.Id);
                    table.CheckConstraint("CK_AnimalWeightMeasurements_Unit_Allowed", "\"Unit\" IN (0, 1)");
                    table.CheckConstraint("CK_AnimalWeightMeasurements_Value_Positive", "\"Value\" > 0");
                    table.ForeignKey(
                        name: "FK_AnimalWeightMeasurements_Animals_AnimalId",
                        column: x => x.AnimalId,
                        principalTable: "Animals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnimalWeightMeasurements_AnimalId_MeasurementDate_RecordedAtUtc_Id",
                table: "AnimalWeightMeasurements",
                columns: new[] { "AnimalId", "MeasurementDate", "RecordedAtUtc", "Id" },
                descending: new[] { false, true, true, false });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnimalWeightMeasurements");
        }
    }
}