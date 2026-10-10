using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Xerp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DefaultWarehouseStockBalance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDefault",
                table: "Warehouses",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Spec 011, R29: every tenant that exists gets a default warehouse - its oldest active warehouse.
            // Set-based, and each tenant decides from its own rows only (T6).
            migrationBuilder.Sql("""
                UPDATE "Warehouses" w SET "IsDefault" = TRUE
                FROM (
                    SELECT DISTINCT ON ("TenantId") "TenantId", "Id"
                    FROM "Warehouses"
                    WHERE "IsActive"
                    ORDER BY "TenantId", "CreatedAt", "Id"
                ) oldest
                WHERE w."TenantId" = oldest."TenantId" AND w."Id" = oldest."Id";
                """);

            // R29: a tenant without an active warehouse gets a new one as provisioning creates it (R1) - code
            // CENTRAL, or CENTRAL-2, CENTRAL-3, ... when that code is taken - written by its oldest API key.
            // (A tenant always has a key: it is created with one and keys are never deleted.)
            migrationBuilder.Sql("""
                INSERT INTO "Warehouses" ("Id", "TenantId", "Code", "Name", "IsActive", "IsDefault", "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy")
                SELECT uuidv7(), t."Id", free."Code", 'Central warehouse', TRUE, TRUE, now(), now(), first_key."Id", first_key."Id"
                FROM "Tenants" t
                CROSS JOIN LATERAL (
                    SELECT k."Id" FROM "ApiKeys" k WHERE k."TenantId" = t."Id" ORDER BY k."CreatedAt", k."Id" LIMIT 1
                ) first_key
                CROSS JOIN LATERAL (
                    SELECT candidate."Code"
                    FROM (
                        SELECT n, CASE WHEN n = 1 THEN 'CENTRAL' ELSE 'CENTRAL-' || n END AS "Code"
                        FROM generate_series(1, (SELECT count(*) + 1 FROM "Warehouses" taken WHERE taken."TenantId" = t."Id")) AS n
                    ) candidate
                    WHERE NOT EXISTS (
                        SELECT 1 FROM "Warehouses" taken WHERE taken."TenantId" = t."Id" AND taken."CodeLower" = lower(candidate."Code"))
                    ORDER BY candidate.n
                    LIMIT 1
                ) free
                WHERE NOT EXISTS (SELECT 1 FROM "Warehouses" w WHERE w."TenantId" = t."Id" AND w."IsDefault");
                """);

            migrationBuilder.CreateTable(
                name: "StockBalances",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArticleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockBalances", x => new { x.TenantId, x.WarehouseId, x.ArticleId });
                    table.CheckConstraint("CK_StockBalances_QuantityNotNegative", "\"Quantity\" >= 0");
                    table.ForeignKey(
                        name: "FK_StockBalances_Articles_TenantId_ArticleId",
                        columns: x => new { x.TenantId, x.ArticleId },
                        principalTable: "Articles",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockBalances_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockBalances_Warehouses_TenantId_WarehouseId",
                        columns: x => new { x.TenantId, x.WarehouseId },
                        principalTable: "Warehouses",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            // Spec 011, R30 / ADR-0018, decision 9: the first rebuild. The stored balance of every pair is the sum
            // of its ledger entries, tenant by tenant (TenantId is part of the group and of the key).
            migrationBuilder.Sql("""
                INSERT INTO "StockBalances" ("TenantId", "WarehouseId", "ArticleId", "Quantity")
                SELECT "TenantId", "WarehouseId", "ArticleId", SUM("Quantity")
                FROM "StockLedgerEntries"
                GROUP BY "TenantId", "WarehouseId", "ArticleId";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Warehouses_TenantId_Default",
                table: "Warehouses",
                column: "TenantId",
                unique: true,
                filter: "\"IsDefault\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Warehouses_DefaultIsActive",
                table: "Warehouses",
                sql: "NOT \"IsDefault\" OR \"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "IX_StockBalances_TenantId_ArticleId",
                table: "StockBalances",
                columns: new[] { "TenantId", "ArticleId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StockBalances");

            migrationBuilder.DropIndex(
                name: "IX_Warehouses_TenantId_Default",
                table: "Warehouses");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Warehouses_DefaultIsActive",
                table: "Warehouses");

            migrationBuilder.DropColumn(
                name: "IsDefault",
                table: "Warehouses");
        }
    }
}
