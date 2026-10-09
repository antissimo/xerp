using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Xerp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ItemUnitConversions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "BaseQuantity",
                table: "StockDocumentLines",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Factor",
                table: "StockDocumentLines",
                type: "numeric(12,6)",
                precision: 12,
                scale: 6,
                nullable: true);

            // Spec 007, section 3: every existing line was entered in its article's base unit. The column is
            // added empty, filled, and only then made required - there is no default unit.
            migrationBuilder.AddColumn<Guid>(
                name: "UnitId",
                table: "StockDocumentLines",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "StockDocumentLines" AS l
                SET "UnitId" = a."BaseUnitId"
                FROM "Articles" AS a
                WHERE a."TenantId" = l."TenantId" AND a."Id" = l."ArticleId";
                """);

            // Every existing posted line (on a posted or a reversed document) has factor 1 and a base quantity
            // equal to its quantity: the ledger entries it produced are that quantity. Draft lines keep both
            // empty; they are converted when read and when posted.
            migrationBuilder.Sql(
                """
                UPDATE "StockDocumentLines" AS l
                SET "Factor" = 1, "BaseQuantity" = l."Quantity"
                FROM "StockDocuments" AS d
                WHERE d."TenantId" = l."TenantId" AND d."Id" = l."DocumentId" AND d."Status" <> 'draft';
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "UnitId",
                table: "StockDocumentLines",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "ArticleUnits",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArticleId = table.Column<Guid>(type: "uuid", nullable: false),
                    UnitId = table.Column<Guid>(type: "uuid", nullable: false),
                    Factor = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArticleUnits", x => new { x.TenantId, x.ArticleId, x.UnitId });
                    table.ForeignKey(
                        name: "FK_ArticleUnits_ApiKeys_TenantId_CreatedBy",
                        columns: x => new { x.TenantId, x.CreatedBy },
                        principalTable: "ApiKeys",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ArticleUnits_ApiKeys_TenantId_UpdatedBy",
                        columns: x => new { x.TenantId, x.UpdatedBy },
                        principalTable: "ApiKeys",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ArticleUnits_Articles_TenantId_ArticleId",
                        columns: x => new { x.TenantId, x.ArticleId },
                        principalTable: "Articles",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ArticleUnits_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ArticleUnits_UnitsOfMeasure_TenantId_UnitId",
                        columns: x => new { x.TenantId, x.UnitId },
                        principalTable: "UnitsOfMeasure",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockDocumentLines_TenantId_UnitId",
                table: "StockDocumentLines",
                columns: new[] { "TenantId", "UnitId" });

            migrationBuilder.CreateIndex(
                name: "IX_ArticleUnits_TenantId_CreatedBy",
                table: "ArticleUnits",
                columns: new[] { "TenantId", "CreatedBy" });

            migrationBuilder.CreateIndex(
                name: "IX_ArticleUnits_TenantId_UnitId",
                table: "ArticleUnits",
                columns: new[] { "TenantId", "UnitId" });

            migrationBuilder.CreateIndex(
                name: "IX_ArticleUnits_TenantId_UpdatedBy",
                table: "ArticleUnits",
                columns: new[] { "TenantId", "UpdatedBy" });

            migrationBuilder.AddForeignKey(
                name: "FK_StockDocumentLines_UnitsOfMeasure_TenantId_UnitId",
                table: "StockDocumentLines",
                columns: new[] { "TenantId", "UnitId" },
                principalTable: "UnitsOfMeasure",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StockDocumentLines_UnitsOfMeasure_TenantId_UnitId",
                table: "StockDocumentLines");

            migrationBuilder.DropTable(
                name: "ArticleUnits");

            migrationBuilder.DropIndex(
                name: "IX_StockDocumentLines_TenantId_UnitId",
                table: "StockDocumentLines");

            migrationBuilder.DropColumn(
                name: "BaseQuantity",
                table: "StockDocumentLines");

            migrationBuilder.DropColumn(
                name: "Factor",
                table: "StockDocumentLines");

            migrationBuilder.DropColumn(
                name: "UnitId",
                table: "StockDocumentLines");
        }
    }
}
