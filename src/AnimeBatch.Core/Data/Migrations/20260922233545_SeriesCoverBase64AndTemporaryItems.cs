using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnimeBatch.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SeriesCoverBase64AndTemporaryItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CoverImageBase64",
                table: "Series",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsTemporary",
                table: "JobItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoverImageBase64",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "IsTemporary",
                table: "JobItems");
        }
    }
}
