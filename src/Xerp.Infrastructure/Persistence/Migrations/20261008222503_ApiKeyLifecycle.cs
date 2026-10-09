using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Xerp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApiKeyLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CreatedBy",
                table: "ApiKeys",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RevokedAt",
                table: "ApiKeys",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RevokedBy",
                table: "ApiKeys",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_TenantId_CreatedBy",
                table: "ApiKeys",
                columns: new[] { "TenantId", "CreatedBy" });

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_TenantId_RevokedBy",
                table: "ApiKeys",
                columns: new[] { "TenantId", "RevokedBy" });

            migrationBuilder.AddForeignKey(
                name: "FK_ApiKeys_ApiKeys_TenantId_CreatedBy",
                table: "ApiKeys",
                columns: new[] { "TenantId", "CreatedBy" },
                principalTable: "ApiKeys",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ApiKeys_ApiKeys_TenantId_RevokedBy",
                table: "ApiKeys",
                columns: new[] { "TenantId", "RevokedBy" },
                principalTable: "ApiKeys",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ApiKeys_ApiKeys_TenantId_CreatedBy",
                table: "ApiKeys");

            migrationBuilder.DropForeignKey(
                name: "FK_ApiKeys_ApiKeys_TenantId_RevokedBy",
                table: "ApiKeys");

            migrationBuilder.DropIndex(
                name: "IX_ApiKeys_TenantId_CreatedBy",
                table: "ApiKeys");

            migrationBuilder.DropIndex(
                name: "IX_ApiKeys_TenantId_RevokedBy",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "RevokedAt",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "RevokedBy",
                table: "ApiKeys");
        }
    }
}
