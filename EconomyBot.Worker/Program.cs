using EconomyBot.Worker.Core;
using EconomyBot.Worker.Configuration;
using EconomyBot.Worker.Features;
using EconomyBot.Worker.Services;

var builder = Host.CreateApplicationBuilder(args);

// Bind EconomyOptions from appsettings.json
builder.Services.Configure<EconomyOptions>(builder.Configuration.GetSection(EconomyOptions.SectionName));

// Register Core Components
builder.Services.AddSingleton<CommandQueue>();
builder.Services.AddSingleton<NotificationQueue>();

// Register Services
builder.Services.AddSingleton<RedisService>();
builder.Services.AddSingleton<PostgresService>();
builder.Services.AddSingleton<JobService>();
builder.Services.AddSingleton<MarketService>();
builder.Services.AddSingleton<RentService>();
builder.Services.AddSingleton<TierService>();
builder.Services.AddSingleton<RicoAiService>();

// Register the Background Services
builder.Services.AddHostedService<TickEngine>();
builder.Services.AddHostedService<DbSyncService>();
builder.Services.AddHostedService<TelegramListenerService>();
builder.Services.AddHostedService<CeremonyBackgroundService>();

// Register Command Features
builder.Services.AddSingleton<ICommandFeature, EconomyBot.Worker.Features.BalanceFeature>();
builder.Services.AddSingleton<ICommandFeature, EconomyBot.Worker.Features.AccountFeature>();
builder.Services.AddSingleton<ICommandFeature, EconomyBot.Worker.Features.SalaryFeature>();
builder.Services.AddSingleton<ICommandFeature, EconomyBot.Worker.Features.JobUpgradeFeature>();
builder.Services.AddSingleton<ICommandFeature, EconomyBot.Worker.Features.CoinFlipFeature>();
builder.Services.AddSingleton<ICommandFeature, EconomyBot.Worker.Features.StealFeature>();
builder.Services.AddSingleton<ICommandFeature, EconomyBot.Worker.Features.EnergyFeature>();
builder.Services.AddSingleton<ICommandFeature, EconomyBot.Worker.Features.HelpFeature>();
builder.Services.AddSingleton<ICommandFeature, EconomyBot.Worker.Features.MarketFeature>();

builder.Services.AddSingleton<ICommandFeature, EconomyBot.Worker.Features.WheelFeature>();
builder.Services.AddSingleton<ICommandFeature, EconomyBot.Worker.Features.TreasureFeature>();
builder.Services.AddSingleton<ICommandFeature, EconomyBot.Worker.Features.InvestFeature>();
builder.Services.AddSingleton<ICommandFeature, EconomyBot.Worker.Features.RentFeature>();
builder.Services.AddSingleton<ICommandFeature, EconomyBot.Worker.Features.RichestFeature>();
builder.Services.AddSingleton<ICommandFeature, EconomyBot.Worker.Features.GenderFeature>();
builder.Services.AddSingleton<ICommandFeature, EconomyBot.Worker.Features.TiersFeature>();
builder.Services.AddSingleton<ICommandFeature, EconomyBot.Worker.Features.DareFeature>();
builder.Services.AddSingleton<ICommandFeature, EconomyBot.Worker.Features.BribeFeature>();
builder.Services.AddSingleton<ICommandFeature, EconomyBot.Worker.Features.RaidFeature>();
builder.Services.AddSingleton<ICommandFeature, EconomyBot.Worker.Features.ShieldsFeature>();
builder.Services.AddSingleton<ICommandFeature, EconomyBot.Worker.Features.BoostsFeature>();
builder.Services.AddSingleton<ICommandFeature, EconomyBot.Worker.Features.AvailFeature>();
builder.Services.AddSingleton<ICommandFeature, EconomyBot.Worker.Features.TransferFeature>();
builder.Services.AddSingleton<ICommandFeature, EconomyBot.Worker.Features.CeremonyFeature>();
builder.Services.AddSingleton<ICommandFeature, EconomyBot.Worker.Features.HeistFeature>();
builder.Services.AddSingleton<ICommandFeature, EconomyBot.Worker.Features.ConsumeFeature>();

var host = builder.Build();

// Optional: Initialize Postgres DB Schema on startup
var pgService = host.Services.GetRequiredService<PostgresService>();
var redisService = host.Services.GetRequiredService<RedisService>();
try { 
    await pgService.InitializeSchemaAsync(); 
    var items = await pgService.GetItemsAsync();
    await redisService.CacheItemsAsync(items);
    
    var jobService = host.Services.GetRequiredService<JobService>();
    await jobService.InitializeAsync(pgService);
} catch (Exception ex) { Console.WriteLine($"DB Init failed: {ex.Message}"); }

host.Run();
