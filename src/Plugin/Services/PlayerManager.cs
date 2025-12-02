using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Players;

namespace K4Missions;

public sealed partial class Plugin
{
	/// <summary>
	/// Manages player mission data and progress tracking
	/// </summary>
	public sealed class PlayerManager(PluginConfig config, DatabaseService database, MissionLoader missionLoader, Func<DateTime?> calculateExpiration, Func<MissionPlayer, bool> checkVipStatus)
	{
		private readonly ConcurrentDictionary<ulong, MissionPlayer> _players = new();
		private readonly PluginConfig _config = config;
		private readonly DatabaseService _database = database;
		private readonly MissionLoader _missionLoader = missionLoader;
		private readonly Func<DateTime?> _calculateExpiration = calculateExpiration;
		private readonly Func<MissionPlayer, bool> _checkVipStatus = checkVipStatus;

		private string _currentMapName = string.Empty;

		/// <summary>
		/// Current number of valid (non-bot, non-spectator) players
		/// </summary>
		public int ActivePlayerCount => _players.Values.Count(p => p.IsValid && p.Player.Controller?.Team > Team.Spectator);

		/// <summary>
		/// All registered players
		/// </summary>
		public IEnumerable<MissionPlayer> AllPlayers => _players.Values;

		/// <summary>
		/// Set the current map name
		/// </summary>
		public void SetCurrentMap(string mapName)
		{
			_currentMapName = mapName;
		}

		/// <summary>
		/// Get or create a player record
		/// </summary>
		public MissionPlayer GetOrCreatePlayer(IPlayer player)
		{
			return _players.GetOrAdd(player.SteamID, _ =>
			{
				var missionPlayer = new MissionPlayer
				{
					SteamId = player.SteamID,
					Player = player
				};

				// Load player data asynchronously
				Task.Run(async () => await LoadPlayerDataAsync(missionPlayer));

				return missionPlayer;
			});
		}

		/// <summary>
		/// Get existing player record
		/// </summary>
		public MissionPlayer? GetPlayer(IPlayer player)
		{
			return _players.TryGetValue(player.SteamID, out var missionPlayer) ? missionPlayer : null;
		}

		/// <summary>
		/// Get player by SteamID
		/// </summary>
		public MissionPlayer? GetPlayer(ulong steamId)
		{
			return _players.TryGetValue(steamId, out var missionPlayer) ? missionPlayer : null;
		}

		/// <summary>
		/// Remove player from tracking
		/// </summary>
		public void RemovePlayer(ulong steamId)
		{
			if (_players.TryRemove(steamId, out var player) && player.IsLoaded)
			{
				// Save missions on disconnect
				Task.Run(async () => await _database.UpdateMissionsAsync(player.Missions));
			}
		}

		/// <summary>
		/// Load player's missions from database
		/// </summary>
		private async Task LoadPlayerDataAsync(MissionPlayer player)
		{
			try
			{
				var savedMissions = await _database.GetPlayerMissionsAsync(player.SteamId);

				foreach (var dbMission in savedMissions)
				{
					// Skip expired missions
					if (dbMission.ExpiresAt.HasValue && dbMission.ExpiresAt.Value < DateTime.Now)
					{
						await _database.RemoveMissionsAsync([dbMission.Id]);
						continue;
					}

					player.Missions.Add(new PlayerMission
					{
						Id = dbMission.Id,
						Event = dbMission.Event,
						Target = dbMission.Target,
						Amount = dbMission.Amount,
						Phrase = dbMission.Phrase,
						RewardPhrase = dbMission.RewardPhrase,
						RewardCommands = dbMission.GetRewardCommandsList(),
						Progress = dbMission.Progress,
						IsCompleted = dbMission.Completed,
						ExpiresAt = dbMission.ExpiresAt,
						EventProperties = dbMission.GetEventProperties(),
						MapName = dbMission.MapName,
						Flag = dbMission.Flag
					});
				}

				player.IsLoaded = true;

				// Check VIP status and ensure correct mission count on main thread
				Core.Scheduler.NextWorldUpdate(() =>
				{
					if (!player.IsValid)
						return;

					player.IsVip = _checkVipStatus(player);
					EnsureCorrectMissionCount(player);
				});
			}
			catch (Exception ex)
			{
				Core.Logger.LogError(ex, "Failed to load missions for {SteamId}", player.SteamId);
				player.IsLoaded = true; // Mark as loaded to prevent retry loops
			}
		}

		/// <summary>
		/// Ensure player has the correct number of missions
		/// </summary>
		public void EnsureCorrectMissionCount(MissionPlayer player)
		{
			if (!player.IsLoaded)
				return;

			var requiredCount = player.IsVip ? _config.MissionAmountVip : _config.MissionAmountNormal;
			var currentCount = player.Missions.Count;

			if (currentCount > requiredCount)
			{
				RemoveExcessMissions(player, currentCount - requiredCount);
			}
			else if (currentCount < requiredCount)
			{
				AssignRandomMissions(player, requiredCount - currentCount);
			}
		}

		/// <summary>
		/// Assign random missions to player
		/// </summary>
		private void AssignRandomMissions(MissionPlayer player, int count)
		{
			// Create a simple key for duplicate detection: Event|Target|Amount|Phrase
			static string GetMissionKey(string evt, string target, int amount, string phrase)
				=> $"{evt}|{target}|{amount}|{phrase}";

			var existingKeys = player.Missions
				.Select(m => GetMissionKey(m.Event, m.Target, m.Amount, m.Phrase))
				.ToHashSet();

			var availableMissions = _missionLoader.GetAvailableMissions(player, flag =>
				Core.Permission.PlayerHasPermission(player.SteamId, flag))
				.Where(m => !existingKeys.Contains(GetMissionKey(m.Event, m.Target, m.Amount, m.Phrase)))
				.ToList();

			if (availableMissions.Count == 0)
				return;

			var random = new Random();
			var addedCount = 0;

			for (var i = 0; i < count && availableMissions.Count > 0; i++)
			{
				var index = random.Next(availableMissions.Count);
				var definition = availableMissions[index];
				availableMissions.RemoveAt(index);

				var expiresAt = _calculateExpiration();
				var mission = definition.CreatePlayerMission(expiresAt);

				// Add to database
				var missionId = Task.Run(async () =>
					await _database.AddMissionAsync(player.SteamId, mission, expiresAt))
					.GetAwaiter().GetResult();

				if (missionId > 0)
				{
					mission.Id = missionId;
					player.Missions.Add(mission);
					addedCount++;
				}
			}

			if (addedCount > 0)
			{
				NotifyNewMissions(player, addedCount);
			}
		}

		/// <summary>
		/// Remove excess missions from player
		/// </summary>
		private void RemoveExcessMissions(MissionPlayer player, int count)
		{
			var toRemove = player.Missions
				.OrderByDescending(m => m.Id)
				.Take(count)
				.ToList();

			foreach (var mission in toRemove)
			{
				player.Missions.Remove(mission);
			}

			Task.Run(async () => await _database.RemoveMissionsAsync(toRemove.Select(m => m.Id)));
		}

		/// <summary>
		/// Notify player of new missions
		/// </summary>
		private void NotifyNewMissions(MissionPlayer player, int count)
		{
			if (!player.IsValid)
				return;

			var command = _config.MissionCommands.FirstOrDefault() ?? "missions";
			var localizer = Core.Translation.GetPlayerLocalizer(player.Player);
			player.Player.SendChat($"{localizer["k4.general.prefix"]} {localizer["k4.missions.new_mission", count, command]}");
		}

		/// <summary>
		/// Process an event for all players
		/// </summary>
		public void ProcessEvent(string eventType, string target, IPlayer player, Dictionary<string, object?>? eventProperties = null)
		{
			// Check minimum player requirement
			if (ActivePlayerCount < _config.MinimumPlayers)
				return;

			var missionPlayer = GetPlayer(player);
			if (missionPlayer == null || !missionPlayer.IsLoaded)
				return;

			ProcessEventForPlayer(missionPlayer, eventType, target, eventProperties);
		}

		/// <summary>
		/// Process an event for a specific player
		/// </summary>
		private void ProcessEventForPlayer(MissionPlayer player, string eventType, string target, Dictionary<string, object?>? eventProperties)
		{
			var matchingMissions = player.Missions
				.Where(m => m.Matches(eventType, target, _currentMapName, eventProperties))
				.ToList();

			foreach (var mission in matchingMissions)
			{
				mission.Progress++;

				if (mission.Progress >= mission.Amount)
				{
					CompleteMission(player, mission);
				}
			}
		}

		/// <summary>
		/// Complete a mission and give rewards
		/// </summary>
		public void CompleteMission(MissionPlayer player, PlayerMission mission)
		{
			if (mission.IsCompleted)
				return;

			mission.IsCompleted = true;

			Task.Run(async () =>
			{
				if (!await _database.CompleteMissionAsync(mission.Id))
					return;

				Core.Scheduler.NextWorldUpdate(() =>
				{
					if (!player.IsValid)
						return;

					// Execute reward commands
					foreach (var command in mission.RewardCommands)
					{
						var replaced = ReplacePlaceholders(player.Player, command);
						Core.Engine.ExecuteCommand(replaced);
					}

					// Notify player
					var localizer = Core.Translation.GetPlayerLocalizer(player.Player);
					player.Player.SendChat($"{localizer["k4.general.prefix"]} {localizer["k4.missions.complete_mission", mission.Phrase, mission.RewardPhrase]}");

					// Fire completion event
					OnMissionCompleted?.Invoke(player, mission);
				});
			});
		}

		/// <summary>
		/// Remove a specific mission from player
		/// </summary>
		public void RemoveMission(MissionPlayer player, PlayerMission mission)
		{
			player.Missions.Remove(mission);
			Task.Run(async () => await _database.RemoveMissionsAsync([mission.Id]));
		}

		/// <summary>
		/// Increment playtime missions for all active players
		/// </summary>
		public void ProcessPlayTime()
		{
			if (ActivePlayerCount < _config.MinimumPlayers)
				return;

			foreach (var player in _players.Values.Where(p => p.IsValid && p.IsLoaded))
			{
				var playtimeMissions = player.Missions
					.Where(m => m.Event == "PlayTime" && !m.IsCompleted)
					.ToList();

				foreach (var mission in playtimeMissions)
				{
					mission.Progress++;

					if (mission.Progress >= mission.Amount)
					{
						CompleteMission(player, mission);
					}
				}
			}
		}

		/// <summary>
		/// Save all player missions to database
		/// </summary>
		public async Task SaveAllPlayersAsync()
		{
			foreach (var player in _players.Values.Where(p => p.IsLoaded && p.Missions.Count > 0))
			{
				await _database.UpdateMissionsAsync(player.Missions);
			}
		}

		/// <summary>
		/// Handle map change
		/// </summary>
		public void OnMapChange(string newMapName)
		{
			SetCurrentMap(newMapName);

			// Re-check mission counts (VIP status may have changed)
			foreach (var player in _players.Values.Where(p => p.IsLoaded && p.IsValid))
			{
				player.IsVip = _checkVipStatus(player);
				EnsureCorrectMissionCount(player);
			}
		}

		/// <summary>
		/// Handle expired missions for a player
		/// </summary>
		public void HandleExpiredMissions(MissionPlayer player)
		{
			var expired = player.Missions.Where(m => m.ExpiresAt.HasValue && m.ExpiresAt.Value < DateTime.Now).ToList();
			if (expired.Count == 0)
				return;

			foreach (var mission in expired)
			{
				player.Missions.Remove(mission);
			}

			var localizer = Core.Translation.GetPlayerLocalizer(player.Player);
			player.Player.SendChat($"{localizer["k4.general.prefix"]} {localizer["k4.missions.dailyreset"]}");

			EnsureCorrectMissionCount(player);
		}

		/// <summary>
		/// Clear all player data
		/// </summary>
		public void Clear()
		{
			_players.Clear();
		}

		/// <summary>
		/// Replace placeholders in command strings
		/// </summary>
		private static string ReplacePlaceholders(IPlayer player, string command)
		{
			var replacements = new Dictionary<string, string>
			{
				{ "{slot}", player.Slot.ToString() },
				{ "{userid}", player.PlayerID.ToString() },
				{ "{name}", player.Controller?.PlayerName ?? "Unknown" },
				{ "{steamid64}", player.SteamID.ToString() },
				{ "{steamid}", player.SteamID.ToString() },
				{ "u0022", "\"" },
			};

			foreach (var (key, value) in replacements)
			{
				command = command.Replace(key, value);
			}

			return command;
		}

		/// <summary>
		/// Event fired when a mission is completed
		/// </summary>
		public event Action<MissionPlayer, PlayerMission>? OnMissionCompleted;
	}
}
