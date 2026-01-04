using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace Oxide.Plugins
{
    [Info("DriveBySedanGangs", "belisario-afk + Gemini + Copilot", "2.5.0")]
    [Description("Spawn sedan gangs via command; sedans stalk players with 3 gang scientists that shoot from the car and on foot, then despawn when too far or dead.")]
    public class DriveBySedanGangs : RustPlugin
    {
        #region Data Types

        private class GangVisuals
        {
            public List<string> Clothing;
            public Dictionary<string, ulong> Skins;
            public string Weapon = "pistol.semiauto";
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

        #region Fields / Constants

        private readonly Dictionary<string, GangVisuals> _gangKits = new Dictionary<string, GangVisuals>();
        private readonly Dictionary<string, string> _borderSpawns = new Dictionary<string, string>();

        // Combat-oriented scientist prefab
        private const string PrefabScientist =
            "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_cargo.prefab";

        private readonly HashSet<ulong> _driveByNPCs = new HashSet<ulong>();

        private const string DefaultGangName = "Westside Pirus";

        private const string SedanPrefab = "assets/content/vehicles/sedan_a/sedantest.entity.prefab";

        private const int DefaultSedansPerPlayer = 1;
        private const float SpawnRadius = 35f;
        private const float FollowUpdateInterval = 0.1f;
        private const float MaxSpeed = 11f;
        private const float Acceleration = 45f;
        private const float BrakeForce = 50f;
        private const float TurnTorque = 14f;
        private const float MaxSteerAngleDeg = 55f;
        private const float MinDistanceToPlayer = 8f;
        private const float TeleportDistance = 300f;    // if car > this, we retire it
        private const float SpawnHeightCheck = 30f;
        private const float SpawnAboveGround = 1.0f;
        private const int GroundLayerMask = -1;

        private const float AttackDistance = 18f;       // car stops & deploys here
        private const float DeployDelaySeconds = 1f;  // slight delay before deploy

        private const float ScientistHealth = 50f;
        private const float ScientistMoveSpeed = 4.5f;

        // Manual drive-by shooting tuning
        private const float MinShootDistance = 10f;
        private const float MaxShootDistance = 60f;
        private const float ShootInterval = 0.4f;

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
            Config["Visuals"] = new Dictionary<string, object>
            {
                ["Westside Pirus"] = new Dictionary<string, object>
                {
                    ["Clothing"] = new List<string> { "hoodie", "pants", "mask.balaclava" },
                    ["Skins"] = new Dictionary<string, object>
                    {
                        ["hoodie"] = 3637124708,
                        ["pants"] = 3637161289,
                        ["mask.balaclava"] = 3637136628
                    }
                },
                ["Northside Vagos"] = new Dictionary<string, object>
                {
                    ["Clothing"] = new List<string> { "hoodie", "pants", "mask.bandana" },
                    ["Skins"] = new Dictionary<string, object>
                    {
                        ["hoodie"] = 3637132959,
                        ["pants"] = 3637162032,
                        ["mask.bandana"] = 3637144551
                    }
                },
                ["Southside Sureños"] = new Dictionary<string, object>
                {
                    ["Clothing"] = new List<string> { "hoodie", "pants", "mask.balaclava" },
                    ["Skins"] = new Dictionary<string, object>
                    {
                        ["hoodie"] = 3637133781,
                        ["pants"] = 3637162360,
                        ["mask.balaclava"] = 3637136303
                    }
                },
                ["Eastside Disciples"] = new Dictionary<string, object>
                {
                    ["Clothing"] = new List<string> { "hoodie", "pants", "mask.bandana" },
                    ["Skins"] = new Dictionary<string, object>
                    {
                        ["hoodie"] = 3637126631,
                        ["pants"] = 3637163268,
                        ["mask.bandana"] = 3637149926
                    }
                }
            };

            Config["Border Spawns"] = new Dictionary<string, object>
            {
                ["Westside Pirus"] = "west",
                ["Northside Vagos"] = "north",
                ["Southside Sureños"] = "south",
                ["Eastside Disciples"] = "east"
            };

            SaveConfig();
        }

        private void LoadGangConfig()
        {
            _gangKits.Clear();

            var visualData = Config["Visuals"] as Dictionary<string, object>;
            if (visualData != null)
            {
                foreach (var kvp in visualData)
                {
                    var data = kvp.Value as Dictionary<string, object>;
                    if (data == null) continue;

                    var clothingList = data["Clothing"] as List<object>;
                    var skinDict = data["Skins"] as Dictionary<string, object>;

                    if (clothingList == null || skinDict == null)
                        continue;

                    _gangKits[kvp.Key] = new GangVisuals
                    {
                        Clothing = clothingList.Select(x => x.ToString()).ToList(),
                        Skins = skinDict.ToDictionary(
                            x => x.Key,
                            x => ulong.Parse(x.Value.ToString())
                        )
                    };
                }
            }

            _borderSpawns.Clear();
            var borderCfg = Config["Border Spawns"] as Dictionary<string, object>;
            if (borderCfg != null)
            {
                foreach (var kvp in borderCfg)
                    _borderSpawns[kvp.Key] = kvp.Value.ToString().ToLower();
            }
        }

        #endregion

        #region Helpers

        private bool FindGroundPosition(Vector3 desired, out Vector3 groundPos)
        {
            groundPos = desired + Vector3.up * SpawnAboveGround;

            Vector3 rayStart = desired + Vector3.up * SpawnHeightCheck;
            RaycastHit hit;
            if (Physics.Raycast(
                    rayStart,
                    Vector3.down,
                    out hit,
                    SpawnHeightCheck * 2f,
                    GroundLayerMask,
                    QueryTriggerInteraction.Ignore))
            {
                groundPos = hit.point + Vector3.up * SpawnAboveGround;
                return true;
            }

            return false;
        }

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
                Puts("[DriveBySedanGangs] ERROR: Failed to create scientist entity.");
                return null;
            }

            var npc = npcEntity as ScientistNPC;
            if (npc == null)
            {
                Puts($"[DriveBySedanGangs] ERROR: Scientist cast failed. Entity type: {npcEntity?.GetType().Name ?? "NULL"}");
                npcEntity.Kill();
                return null;
            }

            npc.Spawn();

            if (npc.net != null)
                _driveByNPCs.Add(npc.net.ID.Value);

            npc.InitializeHealth(ScientistHealth, ScientistHealth);
            npc.startHealth = ScientistHealth;
            npc.SetMaxHealth(ScientistHealth);
            npc.SetHealth(ScientistHealth);

            npc.inventory.Strip();

            if (_gangKits.TryGetValue(gangName, out var kit))
            {
                foreach (var itemShort in kit.Clothing)
                {
                    ulong skin = kit.Skins.ContainsKey(itemShort) ? kit.Skins[itemShort] : 0;
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

        private void OnServerInitialized()
        {
            LoadGangConfig();
            timer.Every(FollowUpdateInterval, UpdateAllSedans);
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
                Mathf.Cos(rad) * SpawnRadius,
                0f,
                Mathf.Sin(rad) * SpawnRadius
            );

            Vector3 samplePos = playerPos + offset;

            if (!FindGroundPosition(samplePos, out var finalPos))
            {
                PrintWarning($"[DriveBySedanGangs] Failed to find ground for sedan spawn near {player.displayName}.");
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

            SeatGangScientistsInSedan(car, DefaultGangName, player);

            return car;
        }

        private void SeatGangScientistsInSedan(BaseEntity car, string gangName, BasePlayer target)
        {
            if (car == null || car.IsDestroyed) return;

            var seats = car.GetComponentsInChildren<BaseMountable>(true);
            if (seats == null || seats.Length == 0)
            {
                Puts("[DriveBySedanGangs] No seats (BaseMountable) found on sedan; cannot seat scientists.");
                return;
            }

            int needed = 3;
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
                    if (npc.Brain.Senses?.Memory != null)
                        npc.Brain.Senses.Memory.SetKnown(target, npc, npc.Brain.Senses);

                    if (npc.Brain.Events?.Memory?.Entity != null)
                        npc.Brain.Events.Memory.Entity.Set(target, 0);
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

            if (Time.realtimeSinceStartup - ev.LastShootTime < ShootInterval) return;

            BasePlayer target = BasePlayer.FindByID(ev.TargetID);
            if (target == null || !target.IsAlive()) return;

            var validShooters = new List<ScientistNPC>();
            for (int i = 0; i < ev.Shooters.Count; i++)
            {
                var npc = ev.Shooters[i];
                if (npc == null || npc.IsDestroyed) continue;
                // Still skip index 0 as "driver" when mounted,
                // but on foot it doesn't hurt – can adjust if needed.
                if (i == 0 && npc.isMounted) continue;
                validShooters.Add(npc);
            }
            if (validShooters.Count == 0) return;

            ev.LastShooterIndex = (ev.LastShooterIndex + 1) % validShooters.Count;
            var shooter = validShooters[ev.LastShooterIndex];
            if (shooter == null || shooter.IsDestroyed) return;

            float distToTarget = Vector3.Distance(shooter.transform.position, target.transform.position);
            if (distToTarget < MinShootDistance || distToTarget > MaxShootDistance) return;

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

            timer.Once(DeployDelaySeconds, () =>
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

            if (!_sedanScientists.TryGetValue(car, out var sciList) || sciList == null || sciList.Count == 0)
                return;

            foreach (var sci in sciList.ToArray())
            {
                if (sci == null || sci.IsDestroyed) continue;

                if (sci.isMounted)
                {
                    BaseMountable seat;
                    if (_scientistSeats.TryGetValue(sci, out seat) && seat != null && !seat.IsDestroyed)
                    {
                        seat.DismountAllPlayers();
                    }
                    else
                    {
                        var mountable = sci.GetMounted() as BaseMountable;
                        if (mountable != null && !mountable.IsDestroyed)
                            mountable.DismountAllPlayers();
                    }
                }

                _scientistSeats.Remove(sci);

                var agent = sci.GetComponent<NavMeshAgent>();
                if (agent != null)
                {
                    agent.enabled = true;
                    agent.stoppingDistance = 5f;
                    agent.speed = ScientistMoveSpeed;
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

                    if (sci.Brain.Senses?.Memory != null)
                        sci.Brain.Senses.Memory.SetKnown(target, sci, sci.Brain.Senses);

                    if (sci.Brain.Events?.Memory?.Entity != null)
                        sci.Brain.Events.Memory.Entity.Set(target, 0);
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

            if (distance <= AttackDistance)
            {
                ScheduleDeploy(car, player);
                return;
            }

            // If sedan falls too far behind, retire it (no teleport)
            if (distance > TeleportDistance)
            {
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
            float steerAmount = Mathf.Clamp(Math.Abs(angleToTarget) / MaxSteerAngleDeg, 0f, 1f) * steerSign;

            var rbMove = car.GetComponent<Rigidbody>();
            if (rbMove != null)
            {
                rbMove.AddTorque(0f, steerAmount * TurnTorque, 0f, ForceMode.Acceleration);
            }

            float desiredSpeed = MaxSpeed;

            if (distance < MinDistanceToPlayer)
            {
                float t = Mathf.InverseLerp(0f, MinDistanceToPlayer, distance);
                desiredSpeed = Mathf.Lerp(MaxSpeed * 0.1f, MaxSpeed * 0.7f, t);
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
                    rbMove.AddForce(forward * Acceleration, ForceMode.Acceleration);
                }
                else
                {
                    if (flatVel.sqrMagnitude > 0.01f)
                    {
                        Vector3 brakeDir = -flatVel.normalized;
                        rbMove.AddForce(brakeDir * BrakeForce, ForceMode.Acceleration);
                    }
                }

                rbMove.AddForce(Vector3.down * 25f, ForceMode.Acceleration);
            }
            else
            {
                car.transform.position += forward * desiredSpeed * FollowUpdateInterval;
            }

            car.SendNetworkUpdate();
        }

        #endregion

        #region Chat Commands

        [ChatCommand("stalksedan")]
        private void CmdStalkSedan(BasePlayer player, string command, string[] args)
        {
            int count = DefaultSedansPerPlayer;
            if (args != null && args.Length > 0)
            {
                int.TryParse(args[0], out count);
                if (count <= 0)
                    count = DefaultSedansPerPlayer;
            }

            EnsureGangForPlayer(player, count);
            player.ChatMessage($"Your drive-by sedan gang is now size {count}.");
        }

        [ChatCommand("destroysedan")]
        private void CmdDestroySedan(BasePlayer player, string command, string[] args)
        {
            DestroyGangForPlayer(player);
            player.ChatMessage("Your drive-by sedan gang has been destroyed.");
        }

        #endregion
    }
}