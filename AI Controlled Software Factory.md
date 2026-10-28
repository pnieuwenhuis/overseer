# AI Controlled Software Factory

# Overview

The AI Controlled Software Factory is a center where the human user is controlling a team of AI agents to work on software. The human has a web interface where it can create intents (as a backlog) and then work an intent out together with an AI agent asks deep questions about the implementation so a complete spec can be worked out, for a team of agents to pick up. These agents have different roles (designer, developer, tester, reviewer) and work together in parallel if possible. The human keeps the overview of what the agents are doing on the web interface, which includes a dashboard with insights per intent to implement, including phase, what is worked on, time elapsed and the possibility to stop the implementation process.

# Technical setpieces

This is a .NET 10 application with a Blazer frontend. The application installs itself into an Azure subscription that the user owns. There it uses Azure Container Apps for the hosting of the center and Azure Virtual Machines using Linux for having temporary sandboxes to have AI agents work in (Docker inside VMs is needed for TestContainers and Docker Compose for dependencies such as databases). OpenCode is used on these sandboxes as harnass and using the Agent Client Protocol (ACP) the harnass output can be shown to the human in the web interface and can interact with it if needed. Intents, memory and progress are stored into a PostgreSQL database, also hosted on Azure. Use Azure Managed Identities for secure authorization. The Azure CLI is used to create and manage Azure resources. All templated bash scripts are stored as resources in the application executable to be easily managed for later modifications.

# Part 1: The landing of the factory in an Azure Subscription

The center itself is an CLI that the user runs from their computer, and it used their current Azure subscription to create some resources into a resource group at the Azure subscription to set itself up. This means an Azure Container App to host itself and be available online, a PostgreSQL database. Sandboxes using Azure Virtual Machines are created and managed on the fly. It returns with the URL to the public endpoint for the web application and fresh generated username and password. Only one user is allowed to access the web application and no user management is not needed yet. Use ASP.NET Core Authentication for authentication/authorization.

# Part 2: Intent to a Spec, conversation with the user

When the user is logged into the web application portal, to sees a list of created intents, allowed to be filtered on completed/not completed. Active intents that is worked on right now are marked. There is a settings panel to specify with GitHub repostories are part of the software that is build. There is the possibility to start a new repository via the interface. A GitHub fine grained token is needed here (and for other git actions) so the user can also store it here. On the page there is the possibility to create new intents. A new intent starts with a chat window where the user describes what they want. It can then save the intent, and then choose to work on it with an AI agent right now or later. Intents that are in the lists that are not completed can also be started to work on.

When working on an intent with an agent, a sandbox is started with the GitHub repositories that are in content, in their workspace. The AI agents asks deep questions about how to implement the intent using the current code available, web search and the information in the intent. The user works with the agents via the web application portal and does not need to log directly into the sandbox machine.

When the AI agent has enough information that allows to create a spec that can be divided into tasks to pick up by a team (including the roles needed), it writes the spec and this spec will be stored in the database, together with the intent. The user can review this spec in the web application portal and make changes to it if needed.

# Part 3: Let the agent squad work

When the user approves the spec, the tasks can be divided along the AI agents. Some tasks can be blocked by others, but some that be taken in parallel. The agents work in the same branch in their own sandbox that is created for this agent and the task. In the sandbox, the work is done while the local environment is setup including dependencies such as the database. Therefore, the repository should contain a Docker Compose file or it should be created as part of one of the intents. The user can see progress in the web application portal, where it sees an task list and some explaination what the agents are doing. Work also always include writing unit-tests and integrations and check if they work.

# Part 4: Review process

After the development work has been done and the feature is complete, a PR in Github is created. Then an AI Agent will review the code. Review guidelines can be set up in the application web portal as part of the settings. The AI Agent will also try to run the application and test it's API or use Playwright and a headless browser to test it and take some screenshots along the way. These screenshots are added to the pull request.

All review comments can be reviewed by the user, and then could task an AI Agent to process all comments.

When approved by the user, the branch will be merged to main and the intent is finished.
