using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.AI;

namespace Oxide.Plugins
{
    [Info("DriveBySedanGangs", "belisario-afk + Gemini + Copilot", "3.0.0")]
    [Description("Spawn sedan gangs via command; sedans stalk players with 3 gang scientists that shoot from the car and on foot, then despawn when too far or dead.")]
    public class DriveBySedanGangs : RustPlugin
    {
        #region Permissions
        
        private const string PermissionUse = "drivebysedan.use";
        private const string PermissionAdmin = "drivebysedan.admin";
        private const string PermissionNoCooldown = "drivebysedan.nocooldown";
        
        #endregion

        #region Data Types

        private const string DefaultWeapon = "pistol.semiauto";

        private class GangVisuals
        {
            public List<string> Clothing;
            public Dictionary<string, ulong> Skins;
            public string Weapon = DefaultWeapon;
            public ulong WeaponSkin = 0;
        }

        private class DriveByState
        {
            public ulong TargetID;
            public List<ScientistNPC> Shooters = new List<ScientistNPC>();
            public float LastShootTime;
            public int LastShooterIndex = -1;
        }

        #endregion

        #region Configuration

        private class PluginConfig
        {
            [JsonProperty("Default Gang Name")]
            public string DefaultGangName { get; set; } = "Westside Pirus";

            [JsonProperty("Default Sedans Per Player")]
            public int DefaultSedansPerPlayer { get; set; } = 1;

            [JsonProperty("Max Sedans Per Player")]
            public int MaxSedansPerPlayer { get; set; } = 5;

            [JsonProperty("Scientists Per Sedan")]
            public int ScientistsPerSedan { get; set; } = 3;

            [JsonProperty("Spawn Radius")]
            public float SpawnRadius { get; set; } = 35f;

            [JsonProperty("Follow Update Interval (seconds)")]
            public float FollowUpdateInterval { get; set; } = 0.1f;

            [JsonProperty("Max Speed")]
            public float MaxSpeed { get; set; } = 11f;

            [JsonProperty("Acceleration")]
            public float Acceleration { get; set; } = 45f;

            [JsonProperty("Brake Force")]
            public float BrakeForce { get; set; } = 50f;

            [JsonProperty("Turn Torque")]
            public float TurnTorque { get; set; } = 14f;

            [JsonProperty("Max Steer Angle (degrees)")]
            public float MaxSteerAngleDeg { get; set; } = 55f;

            [JsonProperty("Min Distance To Player")]
            public float MinDistanceToPlayer { get; set; } = 8f;

            [JsonProperty("Max Distance Before Retire")]
            public float TeleportDistance { get; set; } = 300f;

            [JsonProperty("Attack Distance (deploy scientists)")]
            public float AttackDistance { get; set; } = 18f;

            [JsonProperty("Deploy Delay (seconds)")]
            public float DeployDelaySeconds { get; set; } = 1f;

            [JsonProperty("Scientist Health")]
            public float ScientistHealth { get; set; } = 50f;

            [JsonProperty("Scientist Move Speed")]
            public float ScientistMoveSpeed { get; set; } = 4.5f;

            [JsonProperty("Min Shoot Distance")]
            public float MinShootDistance { get; set; } = 10f;

            [JsonProperty("Max Shoot Distance")]
            public float MaxShootDistance { get; set; } = 60f;

            [JsonProperty("Shoot Interval (seconds)")]
            public float ShootInterval { get; set; } = 0.4f;

            [JsonProperty("Command Cooldown (seconds)")]
            public float CommandCooldown { get; set; } = 30f;

            [JsonProperty("Spawn Height Check")]
            public float SpawnHeightCheck { get; set; } = 30f;

            [JsonProperty("Spawn Above Ground")]
            public float SpawnAboveGround { get; set; } = 1.0f;

            [JsonProperty("Enable Random Gang Selection")]
            public bool EnableRandomGang { get; set; } = false;

            [JsonProperty("Debug Mode")]
            public bool DebugMode { get; set; } = false;

            [JsonProperty("Gang Visuals")]
            public Dictionary<string, GangVisualsConfig> GangVisuals { get; set; }

            [JsonProperty("Border Spawns")]
            public Dictionary<string, string> BorderSpawns { get; set; }
        }

        private class GangVisualsConfig
        {
            [JsonProperty("Clothing")]
            public List<string> Clothing { get; set; }

            [JsonProperty("Skins")]
            public Dictionary<string, ulong> Skins { get; set; }

            [JsonProperty("Weapon")]
            public string Weapon { get; set; } = DefaultWeapon;

            [JsonProperty("Weapon Skin")]
            public ulong WeaponSkin { get; set; } = 0;
        }

        private PluginConfig _config;

        #endregion

        #region Fields / Constants

        private readonly Dictionary<string, GangVisuals> _gangKits = new Dictionary<string, GangVisuals>();
        private readonly Dictionary<string, string> _borderSpawns = new Dictionary<string, string>();

        // Combat-oriented scientist prefab
        private const string PrefabScientist =
            "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_cargo.prefab";

        private readonly HashSet<ulong> _driveByNPCs = new HashSet<ulong>();

        private const string SedanPrefab = "assets/content/vehicles/sedan_a/sedantest.entity.prefab";
        private const int GroundLayerMask = -1;

        // Cooldown tracking
        private readonly Dictionary<ulong, float> _playerCooldowns = new Dictionary<ulong, float>();

        // playerID -> list of sedans
        private readonly Dictionary<ulong, List<BaseEntity>> _playerSedans =
            new Dictionary<ulong, List<BaseEntity>>();

        // sedan -> scientists owned by that sedan
        private readonly Dictionary<BaseEntity, List<ScientistNPC>> _sedanScientists =
            new Dictionary<BaseEntity, List<ScientistNPC>>();

        // scientist -> seat they are mounted in (for proper, player-like dismount)
        private readonly Dictionary<ScientistNPC, BaseMountable> _scientistSeats =
            new Dictionary<ScientistNPC, BaseMountable>();

        // sedan -> fully deployed (scientists have been dismounted)
        private readonly HashSet<BaseEntity> _deployedSedans =
            new HashSet<BaseEntity>();

        // sedan -> already scheduled deploy timer
        private readonly HashSet<BaseEntity> _deployScheduled =
            new HashSet<BaseEntity>();

        // sedan -> manual shooting state
        private readonly Dictionary<BaseEntity, DriveByState> _driveByStates =
            new Dictionary<BaseEntity, DriveByState>();

        // sedan -> flagged for safe retire (so timers/logic ignore it)
        private readonly HashSet<BaseEntity> _retiringSedans =
            new HashSet<BaseEntity>();

        #endregion

        #region Config & Gang Loading

        protected override void LoadDefaultConfig()
        {
            _config = GetDefaultConfig();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null)
                {
                    LoadDefaultConfig();
                    return;
                }

                // Ensure gang visuals exist
                if (_config.GangVisuals == null || _config.GangVisuals.Count == 0)
                {
                    _config.GangVisuals = GetDefaultConfig().GangVisuals;
                    _config.BorderSpawns = GetDefaultConfig().BorderSpawns;
                    SaveConfig();
                }
            }
            catch
            {
                PrintWarning("Configuration file is invalid; using defaults");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        private PluginConfig GetDefaultConfig()
        {
            return new PluginConfig
            {
                GangVisuals = new Dictionary<string, GangVisualsConfig>
                {
                    ["Westside Pirus"] = new GangVisualsConfig
                    {
                        Clothing = new List<string> { "hoodie", "pants", "mask.balaclava" },
                        Skins = new Dictionary<string, ulong>
                        {
                            ["hoodie"] = 3637124708,
                            ["pants"] = 3637161289,
                            ["mask.balaclava"] = 3637136628
                        },
                        Weapon = "pistol.semiauto",
                        WeaponSkin = 0
                    },
                    ["Northside Vagos"] = new GangVisualsConfig
                    {
                        Clothing = new List<string> { "hoodie", "pants", "mask.bandana" },
                        Skins = new Dictionary<string, ulong>
                        {
                            ["hoodie"] = 3637132959,
                            ["pants"] = 3637162032,
                            ["mask.bandana"] = 3637144551
                        },
                        Weapon = "pistol.revolver",
                        WeaponSkin = 0
                    },
                    ["Southside Sureños"] = new GangVisualsConfig
                    {
                        Clothing = new List<string> { "hoodie", "pants", "mask.balaclava" },
                        Skins = new Dictionary<string, ulong>
                        {
                            ["hoodie"] = 3637133781,
                            ["pants"] = 3637162360,
                            ["mask.balaclava"] = 3637136303
                        },
                        Weapon = "smg.2",
                        WeaponSkin = 0
                    },
                    ["Eastside Disciples"] = new GangVisualsConfig
                    {
                        Clothing = new List<string> { "hoodie", "pants", "mask.bandana" },
                        Skins = new Dictionary<string, ulong>
                        {
                            ["hoodie"] = 3637126631,
                            ["pants"] = 3637163268,
                            ["mask.bandana"] = 3637149926
                        },
                        Weapon = "pistol.python",
                        WeaponSkin = 0
                    }
                },
                BorderSpawns = new Dictionary<string, string>
                {
                    ["Westside Pirus"] = "west",
                    ["Northside Vagos"] = "north",
                    ["Southside Sureños"] = "south",
                    ["Eastside Disciples"] = "east"
                }
            };
        }

        private void LoadGangConfig()
        {
            _gangKits.Clear();

            if (_config?.GangVisuals == null) return;

            foreach (var kvp in _config.GangVisuals)
            {
                var data = kvp.Value;
                if (data?.Clothing == null || data.Skins == null) continue;

                _gangKits[kvp.Key] = new GangVisuals
                {
                    Clothing = data.Clothing,
                    Skins = data.Skins,
                    Weapon = data.Weapon ?? DefaultWeapon,
                    WeaponSkin = data.WeaponSkin
                };
            }

            _borderSpawns.Clear();
            if (_config?.BorderSpawns != null)
            {
                foreach (var kvp in _config.BorderSpawns)
                    _borderSpawns[kvp.Key] = kvp.Value.ToLower();
            }

            LogDebug($"Loaded {_gangKits.Count} gang configurations");
        }

        #endregion

        #region Helpers

        private void LogDebug(string message)
        {
            if (_config?.DebugMode == true)
                Puts($"[DEBUG] {message}");
        }

        private bool FindGroundPosition(Vector3 desired, out Vector3 groundPos)
        {
            groundPos = desired + Vector3.up * _config.SpawnAboveGround;

            Vector3 rayStart = desired + Vector3.up * _config.SpawnHeightCheck;
            if (Physics.Raycast(
                    rayStart,
                    Vector3.down,
                    out RaycastHit hit,
                    _config.SpawnHeightCheck * 2f,
                    GroundLayerMask,
                    QueryTriggerInteraction.Ignore))
            {
                groundPos = hit.point + Vector3.up * _config.SpawnAboveGround;
                return true;
            }

            return false;
        }

        private bool IsOnCooldown(BasePlayer player)
        {
            if (player == null) return true;
            if (permission.UserHasPermission(player.UserIDString, PermissionNoCooldown)) return false;

            if (_playerCooldowns.TryGetValue(player.userID, out float lastUse))
            {
                float elapsed = Time.realtimeSinceStartup - lastUse;
                if (elapsed < _config.CommandCooldown)
                {
                    float remaining = _config.CommandCooldown - elapsed;
                    player.ChatMessage($"<color=#ff6b6b>You must wait {remaining:F0} seconds before using this command again.</color>");
                    return true;
                }
            }
            return false;
        }

        private void SetCooldown(BasePlayer player)
        {
            if (player == null) return;
            _playerCooldowns[player.userID] = Time.realtimeSinceStartup;
        }

        private string GetRandomGang()
        {
            if (_gangKits.Count == 0) return _config.DefaultGangName;
            var keys = _gangKits.Keys.ToList();
            return keys[UnityEngine.Random.Range(0, keys.Count)];
        }

        private string GetGangName() => _config.EnableRandomGang ? GetRandomGang() : _config.DefaultGangName;

        /// <summary>
        /// Safely retire a sedan: stop plugin logic, dismount/kill NPCs, then call Kill() once.
        /// </summary>
        private void RetireSedan(BaseEntity car)
        {
            if (car == null) return;
            if (_retiringSedans.Contains(car)) return;
            _retiringSedans.Add(car);

            NextFrame(() =>
            {
                if (car == null || car.IsDestroyed) return;

                if (_sedanScientists.TryGetValue(car, out var sciList) && sciList != null)
                {
                    foreach (var npc in sciList.ToArray())
                    {
                        if (npc == null) continue;

                        if (npc.isMounted)
                        {
                            BaseMountable seat;
                            if (_scientistSeats.TryGetValue(npc, out seat) && seat != null && !seat.IsDestroyed)
                            {
                                seat.DismountAllPlayers();
                            }
                            else
                            {
                                var mountable = npc.GetMounted() as BaseMountable;
                                if (mountable != null && !mountable.IsDestroyed)
                                    mountable.DismountAllPlayers();
                            }
                        }

                        if (!npc.IsDestroyed)
                            npc.Kill();

                        _scientistSeats.Remove(npc);
                    }

                    _sedanScientists.Remove(car);
                }

                _deployedSedans.Remove(car);
                _deployScheduled.Remove(car);
                _driveByStates.Remove(car);
                _retiringSedans.Remove(car);

                if (!car.IsDestroyed)
                    car.Kill();
            });
        }

        #endregion

        #region Scientist Creation & Death Handling

        private ScientistNPC CreateDressedGangScientist(Vector3 position, Quaternion rotation, string gangName)
        {
            var npcEntity = GameManager.server.CreateEntity(PrefabScientist, position, rotation);
            if (npcEntity == null)
            {
                PrintError("Failed to create scientist entity.");
                return null;
            }

            var npc = npcEntity as ScientistNPC;
            if (npc == null)
            {
                PrintError($"Scientist cast failed. Entity type: {npcEntity.GetType().Name}");
                npcEntity.Kill();
                return null;
            }

            npc.Spawn();

            if (npc.net != null)
                _driveByNPCs.Add(npc.net.ID.Value);

            float health = _config.ScientistHealth;
            npc.InitializeHealth(health, health);
            npc.startHealth = health;
            npc.SetMaxHealth(health);
            npc.SetHealth(health);

            npc.inventory.Strip();

            if (_gangKits.TryGetValue(gangName, out var kit))
            {
                foreach (var itemShort in kit.Clothing)
                {
                    ulong skin = 0;
                    kit.Skins?.TryGetValue(itemShort, out skin);
                    var item = ItemManager.CreateByName(itemShort, 1, skin);
                    if (item != null)
                        npc.inventory.GiveItem(item, npc.inventory.containerWear);
                }

                var weapon = ItemManager.CreateByName(kit.Weapon, 1, kit.WeaponSkin);
                if (weapon != null)
                {
                    npc.inventory.GiveItem(weapon, npc.inventory.containerBelt);
                    npc.UpdateActiveItem(weapon.uid);
                }
            }

            npc.SetPlayerFlag(BasePlayer.PlayerFlags.Relaxed, false);
            npc.SetPlayerFlag(BasePlayer.PlayerFlags.DisplaySash, false);

            var agent = npc.GetComponent<NavMeshAgent>();
            if (agent != null)
            {
                agent.enabled = true;
                agent.speed = 0.1f;
                agent.acceleration = 1f;
                agent.stoppingDistance = 0f;
                agent.autoBraking = true;
            }

            if (npc.Brain != null)
            {
                npc.Brain.SetEnabled(true);
                if (npc.Brain.Navigator != null)
                {
                    npc.Brain.Navigator.CanUseNavMesh = true;
                    npc.Brain.Navigator.CanUseAStar = true;
                    npc.Brain.Navigator.MaxRoamDistanceFromHome = 500f;
                }
            }

            LogDebug($"Created gang scientist for {gangName}");
            return npc;
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            var npc = entity as ScientistNPC;
            if (npc != null)
            {
                if (npc.net != null)
                    _driveByNPCs.Remove(npc.net.ID.Value);

                HandleScientistDeath(npc);
                return;
            }
        }

        private void HandleScientistDeath(ScientistNPC npc)
        {
            if (npc == null) return;

            _scientistSeats.Remove(npc);

            BaseEntity ownerCar = null;

            foreach (var kvp in _sedanScientists)
            {
                var car = kvp.Key;
                var list = kvp.Value;
                if (list == null) continue;

                if (list.Remove(npc))
                {
                    ownerCar = car;
                    break;
                }
            }

            if (ownerCar == null) return;

            if (_sedanScientists.TryGetValue(ownerCar, out var remaining))
            {
                if (remaining == null || remaining.Count == 0)
                {
                    _sedanScientists.Remove(ownerCar);
                    _deployedSedans.Remove(ownerCar);
                    _deployScheduled.Remove(ownerCar);
                    _driveByStates.Remove(ownerCar);
                    _retiringSedans.Remove(ownerCar);

                    if (ownerCar != null && !ownerCar.IsDestroyed)
                        ownerCar.Kill();
                }
            }
        }

        #endregion

        #region Lifecycle

        private void Init()
        {
            // Register permissions
            permission.RegisterPermission(PermissionUse, this);
            permission.RegisterPermission(PermissionAdmin, this);
            permission.RegisterPermission(PermissionNoCooldown, this);
        }

        private void OnServerInitialized()
        {
            LoadGangConfig();
            timer.Every(_config.FollowUpdateInterval, UpdateAllSedans);
            
            Puts($"DriveBySedanGangs v3.0.0 loaded with {_gangKits.Count} gang configurations.");
        }

        private void Unload()
        {
            foreach (var list in _playerSedans.Values.ToArray())
            {
                foreach (var car in list.ToArray())
                {
                    if (car != null && !car.IsDestroyed)
                        car.Kill();
                }
            }

            _playerSedans.Clear();

            foreach (var kvp in _sedanScientists.ToArray())
            {
                var sciList = kvp.Value;
                if (sciList == null) continue;

                foreach (var npc in sciList.ToArray())
                {
                    if (npc != null && !npc.IsDestroyed)
                        npc.Kill();

                    if (npc != null)
                        _scientistSeats.Remove(npc);
                }
            }

            _sedanScientists.Clear();
            _deployedSedans.Clear();
            _deployScheduled.Clear();
            _driveByStates.Clear();
            _retiringSedans.Clear();
            _scientistSeats.Clear();
            _playerCooldowns.Clear();
        }

        #endregion

        #region Hooks

        // NOTE: no auto spawn on init/respawn/disconnect anymore.
        // Only /stalksedan and /destroysedan control gangs.

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            DestroyGangForPlayer(player);
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            var baseEntity = entity as BaseEntity;
            if (baseEntity == null)
                return;

            if (!IsSedan(baseEntity))
                return;

            ulong playerToClean = 0;
            bool needsCleanup = false;

            foreach (var kvp in _playerSedans.ToArray())
            {
                var list = kvp.Value;
                if (list == null)
                    continue;

                if (list.Remove(baseEntity))
                {
                    playerToClean = kvp.Key;
                    needsCleanup = true;
                    break;
                }
            }

            if (needsCleanup)
            {
                if (_playerSedans.TryGetValue(playerToClean, out var list) && (list == null || list.Count == 0))
                    _playerSedans.Remove(playerToClean);
            }

            if (_sedanScientists.TryGetValue(baseEntity, out var sciList))
            {
                foreach (var npc in sciList.ToArray())
                {
                    if (npc != null && !npc.IsDestroyed)
                        npc.Kill();

                    if (npc != null)
                        _scientistSeats.Remove(npc);
                }

                _sedanScientists.Remove(baseEntity);
            }

            _deployedSedans.Remove(baseEntity);
            _deployScheduled.Remove(baseEntity);
            _driveByStates.Remove(baseEntity);
            _retiringSedans.Remove(baseEntity);
        }

        #endregion

        #region Sedan / Gang Management

        private bool IsSedan(BaseEntity ent)
        {
            if (ent == null) return false;
            return ent.ShortPrefabName.Equals("sedantest.entity", StringComparison.OrdinalIgnoreCase)
                   || ent.PrefabName == SedanPrefab;
        }

        private void EnsureGangForPlayer(BasePlayer player, int desiredCount)
        {
            if (player == null || !player.IsConnected)
                return;

            if (!_playerSedans.TryGetValue(player.userID, out var list))
            {
                list = new List<BaseEntity>();
                _playerSedans[player.userID] = list;
            }

            // Clean invalid cars
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] == null || list[i].IsDestroyed)
                    list.RemoveAt(i);
            }

            int missing = desiredCount - list.Count;
            if (missing <= 0)
                return;

            for (int i = 0; i < missing; i++)
            {
                var car = SpawnSedanNearPlayer(player, i, desiredCount);
                if (car != null)
                    list.Add(car);
            }
        }

        private BaseEntity SpawnSedanNearPlayer(BasePlayer player, int indexInGang, int gangSize)
        {
            Vector3 playerPos = player.transform.position;

            float angle = (360f / Mathf.Max(gangSize, 1)) * indexInGang;
            float rad = angle * Mathf.Deg2Rad;

            Vector3 offset = new Vector3(
                Mathf.Cos(rad) * _config.SpawnRadius,
                0f,
                Mathf.Sin(rad) * _config.SpawnRadius
            );

            Vector3 samplePos = playerPos + offset;

            if (!FindGroundPosition(samplePos, out var finalPos))
            {
                PrintWarning($"Failed to find ground for sedan spawn near {player.displayName}.");
                return null;
            }

            Vector3 toPlayer = (playerPos - finalPos);
            toPlayer.y = 0f;
            if (toPlayer.sqrMagnitude < 0.01f)
                toPlayer = -player.transform.forward;

            toPlayer.Normalize();
            Quaternion spawnRot = Quaternion.LookRotation(toPlayer, Vector3.up);

            BaseEntity car = GameManager.server.CreateEntity(SedanPrefab, finalPos, spawnRot, true);
            if (car == null)
            {
                PrintError("Failed to create sedan entity from prefab: " + SedanPrefab);
                return null;
            }

            car.enableSaving = false;
            car.Spawn();

            var rb = car.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic = false;
            }

            car.SendNetworkUpdateImmediate();

            _deployedSedans.Remove(car);
            _deployScheduled.Remove(car);
            _retiringSedans.Remove(car);

            string gangName = GetGangName();
            SeatGangScientistsInSedan(car, gangName, player);
            
            LogDebug($"Spawned sedan for {player.displayName} with gang {gangName}");

            return car;
        }

        private void SeatGangScientistsInSedan(BaseEntity car, string gangName, BasePlayer target)
        {
            if (car == null || car.IsDestroyed) return;

            var seats = car.GetComponentsInChildren<BaseMountable>(true);
            if (seats == null || seats.Length == 0)
            {
                PrintWarning("No seats (BaseMountable) found on sedan; cannot seat scientists.");
                return;
            }

            int needed = _config.ScientistsPerSedan;
            var seated = new List<ScientistNPC>();

            foreach (var seat in seats)
            {
                if (needed <= 0)
                    break;

                if (seat == null || seat.IsDestroyed) continue;
                if (seat.AnyMounted()) continue;

                Vector3 spawnPos = seat.transform.position + Vector3.up * 0.1f;
                var npc = CreateDressedGangScientist(spawnPos, seat.transform.rotation, gangName);
                if (npc == null) continue;

                seat.AttemptMount(npc);

                _scientistSeats[npc] = seat;

                if (npc.Brain != null && target != null)
                {
                    npc.Brain.Senses?.Memory?.SetKnown(target, npc, npc.Brain.Senses);
                    npc.Brain.Events?.Memory?.Entity?.Set(target, 0);
                }

                seated.Add(npc);
                needed--;
            }

            if (seated.Count > 0)
            {
                _sedanScientists[car] = seated;

                _driveByStates[car] = new DriveByState
                {
                    TargetID = target.userID,
                    Shooters = new List<ScientistNPC>(seated),
                    LastShootTime = 0f,
                    LastShooterIndex = -1
                };
                
                LogDebug($"Seated {seated.Count} scientists in sedan for target {target.displayName}");
            }
        }

        private void DestroyGangForPlayer(BasePlayer player)
        {
            if (player == null)
                return;

            if (!_playerSedans.TryGetValue(player.userID, out var list))
                return;

            foreach (var car in list.ToArray())
            {
                if (car != null && !car.IsDestroyed)
                    car.Kill();

                if (car != null && _sedanScientists.TryGetValue(car, out var sciList))
                {
                    foreach (var npc in sciList.ToArray())
                    {
                        if (npc != null && !npc.IsDestroyed)
                            npc.Kill();

                        if (npc != null)
                            _scientistSeats.Remove(npc);
                    }

                    _sedanScientists.Remove(car);
                }

                _deployedSedans.Remove(car);
                _deployScheduled.Remove(car);
                _driveByStates.Remove(car);
                _retiringSedans.Remove(car);
            }

            _playerSedans.Remove(player.userID);
        }

        #endregion

        #region Driving + Deployment + Shooting

        private void UpdateAllSedans()
        {
            if (_playerSedans.Count == 0)
                return;

            var playerIds = new List<ulong>(_playerSedans.Keys);

            foreach (var playerId in playerIds)
            {
                var player = BasePlayer.FindByID(playerId) ?? BasePlayer.FindSleeping(playerId);
                if (player == null || !player.IsConnected || player.IsDead())
                {
                    if (_playerSedans.TryGetValue(playerId, out var listToClean))
                    {
                        foreach (var car in listToClean.ToArray())
                        {
                            if (car != null && !car.IsDestroyed)
                                car.Kill();

                            if (car != null && _sedanScientists.TryGetValue(car, out var sciList))
                            {
                                foreach (var npc in sciList.ToArray())
                                {
                                    if (npc != null && !npc.IsDestroyed)
                                        npc.Kill();

                                    if (npc != null)
                                        _scientistSeats.Remove(npc);
                                }

                                _sedanScientists.Remove(car);
                            }

                            _deployedSedans.Remove(car);
                            _deployScheduled.Remove(car);
                            _driveByStates.Remove(car);
                            _retiringSedans.Remove(car);
                        }
                    }

                    _playerSedans.Remove(playerId);
                    continue;
                }

                if (!_playerSedans.TryGetValue(playerId, out var sedans) || sedans == null)
                    continue;

                var sedanSnapshot = sedans.ToArray();
                var toRemoveFromPlayerList = new List<BaseEntity>();

                foreach (var car in sedanSnapshot)
                {
                    if (car == null || car.IsDestroyed)
                    {
                        toRemoveFromPlayerList.Add(car);
                        continue;
                    }

                    if (_retiringSedans.Contains(car))
                        continue;

                    // Manual shooting both mounted and on-foot
                    if (_driveByStates.TryGetValue(car, out var state))
                    {
                        MakeNPCsShootAtPlayer(car, state);
                    }

                    // If not yet deployed, drive car
                    if (!_deployedSedans.Contains(car))
                    {
                        DriveSedanTowardsPlayer(car, player);
                    }
                    else
                    {
                        // Deployed: keep chase nav updated
                        if (_sedanScientists.TryGetValue(car, out var sciList) && sciList != null)
                        {
                            foreach (var sci in sciList.ToArray())
                            {
                                if (sci == null || sci.IsDestroyed) continue;
                                if (sci.Brain == null || sci.Brain.Navigator == null) continue;

                                sci.Brain.Navigator.SetDestination(player.transform.position, BaseNavigator.NavigationSpeed.Normal);
                            }
                        }
                    }
                }

                foreach (var car in toRemoveFromPlayerList)
                {
                    sedans.Remove(car);
                }

                if (sedans.Count == 0)
                    _playerSedans.Remove(playerId);
            }
        }

        /// <summary>
        /// Manual shooting logic, used both while mounted and on foot.
        /// </summary>
        private void MakeNPCsShootAtPlayer(BaseEntity car, DriveByState ev)
        {
            if (car == null || car.IsDestroyed) return;
            if (_retiringSedans.Contains(car)) return;

            if (Time.realtimeSinceStartup - ev.LastShootTime < _config.ShootInterval) return;

            BasePlayer target = BasePlayer.FindByID(ev.TargetID);
            if (target == null || !target.IsAlive()) return;

            var validShooters = new List<ScientistNPC>();
            for (int i = 0; i < ev.Shooters.Count; i++)
            {
                var npc = ev.Shooters[i];
                if (npc == null || npc.IsDestroyed) continue;
                // Skip index 0 as "driver" when mounted
                if (i == 0 && npc.isMounted) continue;
                validShooters.Add(npc);
            }
            if (validShooters.Count == 0) return;

            ev.LastShooterIndex = (ev.LastShooterIndex + 1) % validShooters.Count;
            var shooter = validShooters[ev.LastShooterIndex];
            if (shooter == null || shooter.IsDestroyed) return;

            float distToTarget = Vector3.Distance(shooter.transform.position, target.transform.position);
            if (distToTarget < _config.MinShootDistance || distToTarget > _config.MaxShootDistance) return;

            Vector3 npcEyes = shooter.eyes?.position ?? (shooter.transform.position + Vector3.up * 1.5f);
            Vector3 targetPos = target.transform.position + Vector3.up * 1.2f;
            int losMask = LayerMask.GetMask("World", "Construction", "Terrain");

            if (Physics.Linecast(npcEyes, targetPos, losMask))
                return;

            Vector3 lookDir = (targetPos - npcEyes).normalized;
            shooter.SetAimDirection(lookDir);

            var heldEntity = shooter.GetHeldEntity() as BaseProjectile;
            if (heldEntity != null)
            {
                if (heldEntity.primaryMagazine.contents <= 0)
                    heldEntity.primaryMagazine.contents = heldEntity.primaryMagazine.capacity;

                shooter.SignalBroadcast(BaseEntity.Signal.Attack, string.Empty);
                heldEntity.ServerUse();

                ev.LastShootTime = Time.realtimeSinceStartup;
            }
        }

        private void ScheduleDeploy(BaseEntity car, BasePlayer target)
        {
            if (car == null || car.IsDestroyed || target == null) return;
            if (_retiringSedans.Contains(car)) return;
            if (_deployScheduled.Contains(car)) return;

            _deployScheduled.Add(car);

            var rb = car.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic = true;
            }

            timer.Once(_config.DeployDelaySeconds, () =>
            {
                if (car == null || car.IsDestroyed) return;
                if (_retiringSedans.Contains(car)) return;
                if (target == null || target.IsDead()) return;

                DeployScientistsFromSedan(car, target);
            });
        }

        private void DeployScientistsFromSedan(BaseEntity car, BasePlayer target)
        {
            if (car == null || car.IsDestroyed || target == null) return;
            if (_retiringSedans.Contains(car)) return;
            if (_deployedSedans.Contains(car)) return;

            _deployedSedans.Add(car);
            LogDebug($"Deploying scientists from sedan for target {target.displayName}");

            if (!_sedanScientists.TryGetValue(car, out var sciList) || sciList == null || sciList.Count == 0)
                return;

            foreach (var sci in sciList.ToArray())
            {
                if (sci == null || sci.IsDestroyed) continue;

                if (sci.isMounted)
                {
                    if (_scientistSeats.TryGetValue(sci, out BaseMountable seat) && seat != null && !seat.IsDestroyed)
                    {
                        seat.DismountAllPlayers();
                    }
                    else
                    {
                        var mountable = sci.GetMounted() as BaseMountable;
                        mountable?.DismountAllPlayers();
                    }
                }

                _scientistSeats.Remove(sci);

                var agent = sci.GetComponent<NavMeshAgent>();
                if (agent != null)
                {
                    agent.enabled = true;
                    agent.stoppingDistance = 5f;
                    agent.speed = _config.ScientistMoveSpeed;
                    agent.acceleration = 8f;
                    agent.autoBraking = true;
                }

                if (sci.Brain != null)
                {
                    sci.Brain.SetEnabled(true);

                    if (sci.Brain.Navigator != null)
                    {
                        sci.Brain.Navigator.CanUseNavMesh = true;
                        sci.Brain.Navigator.CanUseAStar = true;
                        sci.Brain.Navigator.MaxRoamDistanceFromHome = 500f;
                        sci.Brain.Navigator.SetDestination(target.transform.position, BaseNavigator.NavigationSpeed.Normal);
                    }

                    sci.Brain.Senses?.Memory?.SetKnown(target, sci, sci.Brain.Senses);
                    sci.Brain.Events?.Memory?.Entity?.Set(target, 0);
                }

                sci.SetPlayerFlag(BasePlayer.PlayerFlags.Relaxed, false);
                sci.SetPlayerFlag(BasePlayer.PlayerFlags.DisplaySash, false);
            }
        }

        private void DriveSedanTowardsPlayer(BaseEntity car, BasePlayer player)
        {
            if (car == null || car.IsDestroyed || player == null)
                return;

            if (_retiringSedans.Contains(car))
                return;

            Vector3 carPos = car.transform.position;
            Vector3 playerPos = player.transform.position;
            Vector3 toPlayer = playerPos - carPos;

            float distance = toPlayer.magnitude;

            if (distance <= _config.AttackDistance)
            {
                ScheduleDeploy(car, player);
                return;
            }

            // If sedan falls too far behind, retire it
            if (distance > _config.TeleportDistance)
            {
                LogDebug($"Retiring sedan - too far from player ({distance:F0}m)");
                RetireSedan(car);
                return;
            }

            Vector3 flatToPlayer = toPlayer;
            flatToPlayer.y = 0f;

            if (flatToPlayer.sqrMagnitude < 0.25f)
            {
                var rbStop = car.GetComponent<Rigidbody>();
                if (rbStop != null)
                    rbStop.velocity = Vector3.Lerp(rbStop.velocity, Vector3.zero, 0.15f);
                return;
            }

            flatToPlayer.Normalize();

            Vector3 forward = car.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f)
                forward = flatToPlayer;

            forward.Normalize();

            float angleToTarget = Vector3.SignedAngle(forward, flatToPlayer, Vector3.up);
            float steerSign = Mathf.Sign(angleToTarget);
            float steerAmount = Mathf.Clamp(Math.Abs(angleToTarget) / _config.MaxSteerAngleDeg, 0f, 1f) * steerSign;

            var rbMove = car.GetComponent<Rigidbody>();
            if (rbMove != null)
            {
                rbMove.AddTorque(0f, steerAmount * _config.TurnTorque, 0f, ForceMode.Acceleration);
            }

            float desiredSpeed = _config.MaxSpeed;

            if (distance < _config.MinDistanceToPlayer)
            {
                float t = Mathf.InverseLerp(0f, _config.MinDistanceToPlayer, distance);
                desiredSpeed = Mathf.Lerp(_config.MaxSpeed * 0.1f, _config.MaxSpeed * 0.7f, t);
            }

            forward = car.transform.forward;
            forward.y = 0f;
            forward.Normalize();

            if (rbMove != null)
            {
                Vector3 currentVel = rbMove.velocity;
                Vector3 flatVel = currentVel;
                flatVel.y = 0f;
                float currentSpeed = Vector3.Dot(flatVel, forward);

                if (currentSpeed < desiredSpeed)
                {
                    rbMove.AddForce(forward * _config.Acceleration, ForceMode.Acceleration);
                }
                else
                {
                    if (flatVel.sqrMagnitude > 0.01f)
                    {
                        Vector3 brakeDir = -flatVel.normalized;
                        rbMove.AddForce(brakeDir * _config.BrakeForce, ForceMode.Acceleration);
                    }
                }

                rbMove.AddForce(Vector3.down * 25f, ForceMode.Acceleration);
            }
            else
            {
                car.transform.position += forward * desiredSpeed * _config.FollowUpdateInterval;
            }

            car.SendNetworkUpdate();
        }

        #endregion

        #region Chat Commands

        [ChatCommand("stalksedan")]
        private void CmdStalkSedan(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PermissionUse))
            {
                player.ChatMessage("<color=#ff6b6b>You don't have permission to use this command.</color>");
                return;
            }

            if (IsOnCooldown(player))
                return;

            int count = _config.DefaultSedansPerPlayer;
            if (args != null && args.Length > 0)
            {
                if (int.TryParse(args[0], out int parsed) && parsed > 0)
                    count = parsed;
            }

            // Enforce max sedans limit
            count = Mathf.Min(count, _config.MaxSedansPerPlayer);

            SetCooldown(player);
            EnsureGangForPlayer(player, count);
            player.ChatMessage($"<color=#4ecdc4>Your drive-by sedan gang ({count} sedan{(count > 1 ? "s" : "")}) is on the way!</color>");
        }

        [ChatCommand("destroysedan")]
        private void CmdDestroySedan(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PermissionUse))
            {
                player.ChatMessage("<color=#ff6b6b>You don't have permission to use this command.</color>");
                return;
            }

            if (!_playerSedans.ContainsKey(player.userID) || _playerSedans[player.userID].Count == 0)
            {
                player.ChatMessage("<color=#ff6b6b>You don't have any active sedan gangs.</color>");
                return;
            }

            DestroyGangForPlayer(player);
            player.ChatMessage("<color=#4ecdc4>Your drive-by sedan gang has been destroyed.</color>");
        }

        [ChatCommand("sedanstatus")]
        private void CmdSedanStatus(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PermissionUse))
            {
                player.ChatMessage("<color=#ff6b6b>You don't have permission to use this command.</color>");
                return;
            }

            if (!_playerSedans.TryGetValue(player.userID, out var sedans) || sedans == null || sedans.Count == 0)
            {
                player.ChatMessage("<color=#ff9f43>No active sedan gangs.</color>");
                return;
            }

            int totalScientists = 0;
            foreach (var car in sedans)
            {
                if (car != null && _sedanScientists.TryGetValue(car, out var sciList) && sciList != null)
                    totalScientists += sciList.Count(s => s != null && !s.IsDestroyed);
            }

            player.ChatMessage($"<color=#4ecdc4>Active sedan gangs: {sedans.Count}</color>");
            player.ChatMessage($"<color=#4ecdc4>Total gang members: {totalScientists}</color>");
        }

        #endregion

        #region Console Commands

        [ConsoleCommand("driveby.spawn")]
        private void ConsoleCmdSpawn(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
            {
                // Server console or admin executing
                if (!arg.IsAdmin)
                {
                    arg.ReplyWith("You must be an admin to use this command from console.");
                    return;
                }

                if (arg.Args == null || arg.Args.Length < 1)
                {
                    arg.ReplyWith("Usage: driveby.spawn <playerNameOrId> [count]");
                    return;
                }

                var target = FindPlayer(arg.Args[0]);
                if (target == null)
                {
                    arg.ReplyWith($"Player '{arg.Args[0]}' not found.");
                    return;
                }

                int count = _config.DefaultSedansPerPlayer;
                if (arg.Args.Length > 1 && int.TryParse(arg.Args[1], out int parsed) && parsed > 0)
                    count = Mathf.Min(parsed, _config.MaxSedansPerPlayer);

                EnsureGangForPlayer(target, count);
                arg.ReplyWith($"Spawned {count} sedan gang(s) for {target.displayName}.");
                return;
            }

            // Player executing
            if (!permission.UserHasPermission(player.UserIDString, PermissionAdmin))
            {
                arg.ReplyWith("You don't have permission to use this command.");
                return;
            }

            if (arg.Args == null || arg.Args.Length < 1)
            {
                // Spawn for self
                int selfCount = _config.DefaultSedansPerPlayer;
                if (arg.Args != null && arg.Args.Length > 0 && int.TryParse(arg.Args[0], out int selfParsed))
                    selfCount = Mathf.Min(selfParsed, _config.MaxSedansPerPlayer);

                EnsureGangForPlayer(player, selfCount);
                arg.ReplyWith($"Spawned {selfCount} sedan gang(s) for yourself.");
                return;
            }

            var targetPlayer = FindPlayer(arg.Args[0]);
            if (targetPlayer == null)
            {
                arg.ReplyWith($"Player '{arg.Args[0]}' not found.");
                return;
            }

            int targetCount = _config.DefaultSedansPerPlayer;
            if (arg.Args.Length > 1 && int.TryParse(arg.Args[1], out int targetParsed) && targetParsed > 0)
                targetCount = Mathf.Min(targetParsed, _config.MaxSedansPerPlayer);

            EnsureGangForPlayer(targetPlayer, targetCount);
            arg.ReplyWith($"Spawned {targetCount} sedan gang(s) for {targetPlayer.displayName}.");
        }

        [ConsoleCommand("driveby.destroy")]
        private void ConsoleCmdDestroy(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
            {
                if (!arg.IsAdmin)
                {
                    arg.ReplyWith("You must be an admin to use this command from console.");
                    return;
                }

                if (arg.Args == null || arg.Args.Length < 1)
                {
                    arg.ReplyWith("Usage: driveby.destroy <playerNameOrId|all>");
                    return;
                }

                if (arg.Args[0].ToLower() == "all")
                {
                    int count = _playerSedans.Count;
                    foreach (var playerId in _playerSedans.Keys.ToArray())
                    {
                        var targetPlayer = BasePlayer.FindByID(playerId);
                        if (targetPlayer != null)
                            DestroyGangForPlayer(targetPlayer);
                    }
                    arg.ReplyWith($"Destroyed all sedan gangs ({count} players affected).");
                    return;
                }

                var target = FindPlayer(arg.Args[0]);
                if (target == null)
                {
                    arg.ReplyWith($"Player '{arg.Args[0]}' not found.");
                    return;
                }

                DestroyGangForPlayer(target);
                arg.ReplyWith($"Destroyed sedan gang for {target.displayName}.");
                return;
            }

            if (!permission.UserHasPermission(player.UserIDString, PermissionAdmin))
            {
                arg.ReplyWith("You don't have permission to use this command.");
                return;
            }

            if (arg.Args == null || arg.Args.Length < 1)
            {
                DestroyGangForPlayer(player);
                arg.ReplyWith("Destroyed your sedan gang.");
                return;
            }

            if (arg.Args[0].ToLower() == "all")
            {
                int count = _playerSedans.Count;
                foreach (var playerId in _playerSedans.Keys.ToArray())
                {
                    var targetPlayer = BasePlayer.FindByID(playerId);
                    if (targetPlayer != null)
                        DestroyGangForPlayer(targetPlayer);
                }
                arg.ReplyWith($"Destroyed all sedan gangs ({count} players affected).");
                return;
            }

            var targetPlayerForDestroy = FindPlayer(arg.Args[0]);
            if (targetPlayerForDestroy == null)
            {
                arg.ReplyWith($"Player '{arg.Args[0]}' not found.");
                return;
            }

            DestroyGangForPlayer(targetPlayerForDestroy);
            arg.ReplyWith($"Destroyed sedan gang for {targetPlayerForDestroy.displayName}.");
        }

        [ConsoleCommand("driveby.list")]
        private void ConsoleCmdList(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null && !permission.UserHasPermission(player.UserIDString, PermissionAdmin))
            {
                arg.ReplyWith("You don't have permission to use this command.");
                return;
            }

            if (!arg.IsAdmin && player == null)
            {
                arg.ReplyWith("You must be an admin to use this command.");
                return;
            }

            if (_playerSedans.Count == 0)
            {
                arg.ReplyWith("No active sedan gangs.");
                return;
            }

            var response = $"Active sedan gangs ({_playerSedans.Count}):\n";
            foreach (var kvp in _playerSedans)
            {
                var targetPlayer = BasePlayer.FindByID(kvp.Key);
                string playerName = targetPlayer?.displayName ?? kvp.Key.ToString();
                int sedanCount = kvp.Value?.Count(c => c != null && !c.IsDestroyed) ?? 0;
                int scientistCount = 0;

                foreach (var car in kvp.Value)
                {
                    if (car != null && _sedanScientists.TryGetValue(car, out var sciList) && sciList != null)
                        scientistCount += sciList.Count(s => s != null && !s.IsDestroyed);
                }

                response += $"  {playerName}: {sedanCount} sedan(s), {scientistCount} scientist(s)\n";
            }

            arg.ReplyWith(response);
        }

        private BasePlayer FindPlayer(string nameOrId)
        {
            if (ulong.TryParse(nameOrId, out ulong userId))
            {
                return BasePlayer.FindByID(userId) ?? BasePlayer.FindSleeping(userId);
            }

            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player.displayName.IndexOf(nameOrId, StringComparison.OrdinalIgnoreCase) >= 0)
                    return player;
            }

            return null;
        }

        #endregion
    }
}