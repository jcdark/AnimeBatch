using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnimeBatch.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ItemQualityVmaf : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "QualityPsnr",
                table: "JobItems",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "QualitySsim",
                table: "JobItems",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "QualityVmaf",
                table: "JobItems",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QualityPsnr",
                table: "JobItems");

            migrationBuilder.DropColumn(
                name: "QualitySsim",
                table: "JobItems");

            migrationBuilder.DropColumn(
                name: "QualityVmaf",
                table: "JobItems");
        }
    }
}
