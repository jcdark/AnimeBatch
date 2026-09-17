using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnimeBatch.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class UpscaleModeAndCodec : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "UpscaleEnabled",
                table: "Jobs",
                newName: "UpscaleMode");

            migrationBuilder.AddColumn<string>(
                name: "VideoCodec",
                table: "Jobs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VideoCodec",
                table: "Jobs");

            migrationBuilder.RenameColumn(
                name: "UpscaleMode",
                table: "Jobs",
                newName: "UpscaleEnabled");
        }
    }
}
