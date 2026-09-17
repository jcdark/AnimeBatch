using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnimeBatch.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class JobErrorMessage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ErrorMessage",
                table: "Jobs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ErrorMessage",
                table: "Jobs");
        }
    }
}
