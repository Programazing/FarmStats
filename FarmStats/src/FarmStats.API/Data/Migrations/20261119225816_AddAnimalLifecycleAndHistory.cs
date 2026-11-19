using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FarmStats.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAnimalLifecycleAndHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAtUtc",
                table: "Animals",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletionNotes",
                table: "Animals",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeletionReason",
                table: "Animals",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "Revision",
                table: "Animals",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "Animals",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateOnly>(
                name: "StatusEffectiveDate",
                table: "Animals",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StatusNotes",
                table: "Animals",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AnimalHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AnimalId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<int>(type: "integer", nullable: false),
                    PreviousStatus = table.Column<int>(type: "integer", nullable: true),
                    NewStatus = table.Column<int>(type: "integer", nullable: true),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: true),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletionReason = table.Column<int>(type: "integer", nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ActorUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnimalHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnimalHistory_Animals_AnimalId",
                        column: x => x.AnimalId,
                        principalTable: "Animals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Animals_DeletedAtUtc_Status",
                table: "Animals",
                columns: new[] { "DeletedAtUtc", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AnimalHistory_AnimalId_RecordedAtUtc",
                table: "AnimalHistory",
                columns: new[] { "AnimalId", "RecordedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnimalHistory");

            migrationBuilder.DropIndex(
                name: "IX_Animals_DeletedAtUtc_Status",
                table: "Animals");

            migrationBuilder.DropColumn(
                name: "DeletedAtUtc",
                table: "Animals");

            migrationBuilder.DropColumn(
                name: "DeletionNotes",
                table: "Animals");

            migrationBuilder.DropColumn(
                name: "DeletionReason",
                table: "Animals");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "Animals");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Animals");

            migrationBuilder.DropColumn(
                name: "StatusEffectiveDate",
                table: "Animals");

            migrationBuilder.DropColumn(
                name: "StatusNotes",
                table: "Animals");
        }
    }
}
