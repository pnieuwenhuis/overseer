using Overseer.Cli;

return await OverseerCli.InvokeAsync(args, OverseerCli.ProcessEnvironment(), Console.In, Console.Out, Console.Error);
