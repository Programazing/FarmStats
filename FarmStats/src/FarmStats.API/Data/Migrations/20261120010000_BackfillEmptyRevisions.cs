using FarmStats.API.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FarmStats.API.Data.Migrations
{
    [DbContext(typeof(FarmStatsDbContext))]
    [Migration("20261120010000_BackfillEmptyRevisions")]
    public class BackfillEmptyRevisions : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "Animals" SET "Revision" = gen_random_uuid() WHERE "Revision" = '00000000-0000-0000-0000-000000000000';
                UPDATE "Species" SET "Revision" = gen_random_uuid() WHERE "Revision" = '00000000-0000-0000-0000-000000000000';
                UPDATE "Breeds" SET "Revision" = gen_random_uuid() WHERE "Revision" = '00000000-0000-0000-0000-000000000000';
                UPDATE "Areas" SET "Revision" = gen_random_uuid() WHERE "Revision" = '00000000-0000-0000-0000-000000000000';
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}