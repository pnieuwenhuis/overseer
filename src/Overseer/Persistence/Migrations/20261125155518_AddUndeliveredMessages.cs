using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Overseer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUndeliveredMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UndeliveredMessages",
                columns: table => new
                {
                    IntentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskNumber = table.Column<int>(type: "integer", nullable: false),
                    SessionNumber = table.Column<int>(type: "integer", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UndeliveredMessages", x => new { x.IntentId, x.TaskNumber, x.SessionNumber, x.Number });
                    table.ForeignKey(
                        name: "FK_UndeliveredMessages_AgentSessions_IntentId_TaskNumber_Sessi~",
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
                name: "UndeliveredMessages");
        }
    }
}
