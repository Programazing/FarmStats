using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FarmStats.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAnimalNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AnimalNotes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AnimalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnimalNotes", x => x.Id);
                    table.CheckConstraint("CK_AnimalNotes_Text_NotEmpty", "length(btrim(\"Text\")) > 0");
                    table.ForeignKey(
                        name: "FK_AnimalNotes_Animals_AnimalId",
                        column: x => x.AnimalId,
                        principalTable: "Animals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnimalNotes_AnimalId_RecordedAtUtc_Id",
                table: "AnimalNotes",
                columns: new[] { "AnimalId", "RecordedAtUtc", "Id" },
                descending: new[] { false, true, false });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnimalNotes");
        }
    }
}
