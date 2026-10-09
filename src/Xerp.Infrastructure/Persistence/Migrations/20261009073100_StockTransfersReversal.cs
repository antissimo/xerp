using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Xerp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StockTransfersReversal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ReversalOfId",
                table: "StockDocuments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReversedById",
                table: "StockDocuments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ToWarehouseId",
                table: "StockDocuments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockDocuments_TenantId_ReversalOfId",
                table: "StockDocuments",
                columns: new[] { "TenantId", "ReversalOfId" },
                unique: true,
                filter: "\"ReversalOfId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StockDocuments_TenantId_ReversedById",
                table: "StockDocuments",
                columns: new[] { "TenantId", "ReversedById" });

            migrationBuilder.CreateIndex(
                name: "IX_StockDocuments_TenantId_ToWarehouseId",
                table: "StockDocuments",
                columns: new[] { "TenantId", "ToWarehouseId" });

            migrationBuilder.AddForeignKey(
                name: "FK_StockDocuments_StockDocuments_TenantId_ReversalOfId",
                table: "StockDocuments",
                columns: new[] { "TenantId", "ReversalOfId" },
                principalTable: "StockDocuments",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StockDocuments_StockDocuments_TenantId_ReversedById",
                table: "StockDocuments",
                columns: new[] { "TenantId", "ReversedById" },
                principalTable: "StockDocuments",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StockDocuments_Warehouses_TenantId_ToWarehouseId",
                table: "StockDocuments",
                columns: new[] { "TenantId", "ToWarehouseId" },
                principalTable: "Warehouses",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StockDocuments_StockDocuments_TenantId_ReversalOfId",
                table: "StockDocuments");

            migrationBuilder.DropForeignKey(
                name: "FK_StockDocuments_StockDocuments_TenantId_ReversedById",
                table: "StockDocuments");

            migrationBuilder.DropForeignKey(
                name: "FK_StockDocuments_Warehouses_TenantId_ToWarehouseId",
                table: "StockDocuments");

            migrationBuilder.DropIndex(
                name: "IX_StockDocuments_TenantId_ReversalOfId",
                table: "StockDocuments");

            migrationBuilder.DropIndex(
                name: "IX_StockDocuments_TenantId_ReversedById",
                table: "StockDocuments");

            migrationBuilder.DropIndex(
                name: "IX_StockDocuments_TenantId_ToWarehouseId",
                table: "StockDocuments");

            migrationBuilder.DropColumn(
                name: "ReversalOfId",
                table: "StockDocuments");

            migrationBuilder.DropColumn(
                name: "ReversedById",
                table: "StockDocuments");

            migrationBuilder.DropColumn(
                name: "ToWarehouseId",
                table: "StockDocuments");
        }
    }
}
