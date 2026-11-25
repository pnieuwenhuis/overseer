using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Overseer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentSessionLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentSessions",
                columns: table => new
                {
                    IntentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskNumber = table.Column<int>(type: "integer", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentSessions", x => new { x.IntentId, x.TaskNumber, x.Number });
                    table.ForeignKey(
                        name: "FK_AgentSessions_IntentTasks_IntentId_TaskNumber",
                        columns: x => new { x.IntentId, x.TaskNumber },
                        principalTable: "IntentTasks",
                        principalColumns: new[] { "IntentId", "Number" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentSessionEntries",
                columns: table => new
                {
                    IntentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskNumber = table.Column<int>(type: "integer", nullable: false),
                    SessionNumber = table.Column<int>(type: "integer", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentSessionEntries", x => new { x.IntentId, x.TaskNumber, x.SessionNumber, x.Number });
                    table.ForeignKey(
                        name: "FK_AgentSessionEntries_AgentSessions_IntentId_TaskNumber_Sessi~",
                        columns: x => new { x.IntentId, x.TaskNumber, x.SessionNumber },
                        principalTable: "AgentSessions",
                        principalColumns: new[] { "IntentId", "TaskNumber", "Number" },
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentSessionEntries");

            migrationBuilder.DropTable(
                name: "AgentSessions");
        }
    }
}
