using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnimeBatch.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class UpscaleTmdbHistoryRename : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Renomeações SEMÂNTICAS (o scaffold automático do EF pareia por posição e
            // trocaria os valores entre colunas):
            //   MinKbps (capítulos normais)   → EpisodeKbps
            //   MaxKbps (abertura)            → OpeningKbps
            //   EndKbps (encerramento)        → EndingKbps
            migrationBuilder.RenameColumn(
                name: "MinKbps",
                table: "Series",
                newName: "EpisodeKbps");

            migrationBuilder.RenameColumn(
                name: "MaxKbps",
                table: "Series",
                newName: "OpeningKbps");

            migrationBuilder.RenameColumn(
                name: "EndKbps",
                table: "Series",
                newName: "EndingKbps");

            migrationBuilder.RenameColumn(
                name: "MinKbps",
                table: "Jobs",
                newName: "EpisodeKbps");

            migrationBuilder.RenameColumn(
                name: "MaxKbps",
                table: "Jobs",
                newName: "OpeningKbps");

            migrationBuilder.RenameColumn(
                name: "EndKbps",
                table: "Jobs",
                newName: "EndingKbps");

            // TMDB nas séries
            migrationBuilder.AddColumn<int>(
                name: "TmdbId",
                table: "Series",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PosterPath",
                table: "Series",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Overview",
                table: "Series",
                type: "TEXT",
                nullable: true);

            // Upscale nos jobs (UpscaleOptionsJson nunca foi usado — todos null)
            migrationBuilder.DropColumn(
                name: "UpscaleOptionsJson",
                table: "Jobs");

            migrationBuilder.AddColumn<bool>(
                name: "UpscaleEnabled",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "UpscaleModel",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UpscaleTargetHeight",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

            // Histórico de conversões
            migrationBuilder.CreateTable(
                name: "ConversionRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: true),
                    SeriesName = table.Column<string>(type: "TEXT", nullable: false),
                    ConvertedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    OutputPath = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversionRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConversionRecords_SeriesId",
                table: "ConversionRecords",
                column: "SeriesId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConversionRecords");

            migrationBuilder.DropColumn(
                name: "UpscaleEnabled",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "UpscaleModel",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "UpscaleTargetHeight",
                table: "Jobs");

            migrationBuilder.AddColumn<string>(
                name: "UpscaleOptionsJson",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.DropColumn(
                name: "TmdbId",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "PosterPath",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "Overview",
                table: "Series");

            migrationBuilder.RenameColumn(
                name: "EpisodeKbps",
                table: "Series",
                newName: "MinKbps");

            migrationBuilder.RenameColumn(
                name: "OpeningKbps",
                table: "Series",
                newName: "MaxKbps");

            migrationBuilder.RenameColumn(
                name: "EndingKbps",
                table: "Series",
                newName: "EndKbps");

            migrationBuilder.RenameColumn(
                name: "EpisodeKbps",
                table: "Jobs",
                newName: "MinKbps");

            migrationBuilder.RenameColumn(
                name: "OpeningKbps",
                table: "Jobs",
                newName: "MaxKbps");

            migrationBuilder.RenameColumn(
                name: "EndingKbps",
                table: "Jobs",
                newName: "EndKbps");
        }
    }
}
