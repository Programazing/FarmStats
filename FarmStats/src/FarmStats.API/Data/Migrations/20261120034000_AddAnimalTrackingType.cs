using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FarmStats.API.Data.Migrations;

public partial class AddAnimalTrackingType : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Preserve legacy records without inferring Individual/Group from quantity.
        migrationBuilder.AddColumn<int>(
            name: "TrackingType",
            table: "Animals",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddCheckConstraint(
            name: "CK_Animals_TrackingType_Quantity",
            table: "Animals",
            sql: "(\"TrackingType\" = 0 AND \"Quantity\" >= 1) OR (\"TrackingType\" = 1 AND \"Quantity\" = 1) OR (\"TrackingType\" = 2 AND \"Quantity\" >= 0)");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_Animals_TrackingType_Quantity",
            table: "Animals");

        migrationBuilder.DropColumn(name: "TrackingType", table: "Animals");
    }
}
