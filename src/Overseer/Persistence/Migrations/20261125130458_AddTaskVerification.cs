using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Overseer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "AwaitingPush",
                table: "IntentTasks",
                type: "text[]",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "VerificationAttempts",
                columns: table => new
                {
                    IntentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskNumber = table.Column<int>(type: "integer", nullable: false),
                    Run = table.Column<int>(type: "integer", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VerificationAttempts", x => new { x.IntentId, x.TaskNumber, x.Run, x.Number });
                    table.ForeignKey(
                        name: "FK_VerificationAttempts_IntentTasks_IntentId_TaskNumber",
                        columns: x => new { x.IntentId, x.TaskNumber },
                        principalTable: "IntentTasks",
                        principalColumns: new[] { "IntentId", "Number" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VerificationSteps",
                columns: table => new
                {
                    IntentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskNumber = table.Column<int>(type: "integer", nullable: false),
                    Run = table.Column<int>(type: "integer", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Repository = table.Column<string>(type: "text", nullable: false),
                    What = table.Column<string>(type: "text", nullable: false),
                    Command = table.Column<string>(type: "text", nullable: true),
                    Failure = table.Column<string>(type: "text", nullable: true),
                    Output = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VerificationSteps", x => new { x.IntentId, x.TaskNumber, x.Run, x.AttemptNumber, x.Number });
                    table.ForeignKey(
                        name: "FK_VerificationSteps_VerificationAttempts_IntentId_TaskNumber_~",
                        columns: x => new { x.IntentId, x.TaskNumber, x.Run, x.AttemptNumber },
                        principalTable: "VerificationAttempts",
                        principalColumns: new[] { "IntentId", "TaskNumber", "Run", "Number" },
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VerificationSteps");

            migrationBuilder.DropTable(
                name: "VerificationAttempts");

            migrationBuilder.DropColumn(
                name: "AwaitingPush",
                table: "IntentTasks");
        }
    }
}
