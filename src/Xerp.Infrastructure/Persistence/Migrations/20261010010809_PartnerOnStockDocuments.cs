using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Xerp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PartnerOnStockDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PartnerId",
                table: "StockDocuments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockDocuments_TenantId_PartnerId",
                table: "StockDocuments",
                columns: new[] { "TenantId", "PartnerId" });

            migrationBuilder.AddForeignKey(
                name: "FK_StockDocuments_Partners_TenantId_PartnerId",
                table: "StockDocuments",
                columns: new[] { "TenantId", "PartnerId" },
                principalTable: "Partners",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            // Spec 011a, R17: every linked stock document - drafts, posted, reversed and reversing ones - gets
            // the partner of its order; an unlinked one keeps none. Set-based, each row from its own tenant's order.
            migrationBuilder.Sql("""
                UPDATE "StockDocuments" d SET "PartnerId" = o."SupplierId"
                FROM "PurchaseOrders" o
                WHERE o."TenantId" = d."TenantId" AND o."Id" = d."PurchaseOrderId";
                """);
            migrationBuilder.Sql("""
                UPDATE "StockDocuments" d SET "PartnerId" = o."CustomerId"
                FROM "SalesOrders" o
                WHERE o."TenantId" = d."TenantId" AND o."Id" = d."SalesOrderId";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StockDocuments_Partners_TenantId_PartnerId",
                table: "StockDocuments");

            migrationBuilder.DropIndex(
                name: "IX_StockDocuments_TenantId_PartnerId",
                table: "StockDocuments");

            migrationBuilder.DropColumn(
                name: "PartnerId",
                table: "StockDocuments");
        }
    }
}
