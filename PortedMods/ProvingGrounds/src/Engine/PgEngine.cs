using System.Globalization;
using System.Numerics;
using System.Text;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Items;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;

namespace ProvingGrounds.Engine;

// The engine adapter (task 2.2): every read and write of the game the original makes through Kenshi's engine goes through one method here.
// Meitou 0.17.3 (tag v0.17.3, fa34a1b); paths relative to its repo root. Every method below uses PUBLIC Meitou API only (no by-name lookup;
// PgCheck engine JIT-binds these methods against the real 0.17.3 DLLs), except EndCombat (task 6.2): the internal CombatSystem.Disengage
// through Members.CombatDisengage (checked by PgCheck hooks and engine's MissingEngine).
//
// THE TO-DO: every engine use of the original, PA §3.1 (hooks) + PA §3.2 (calls) + TW §3 (town needs), walked bullet by bullet and grouped
// as PA §3.2 does, hooks under the group they serve. "done: X" = the adapter method here; "task n.m" = the task of the plan
// (proving-grounds-port/M2..M8) that builds it (its glue adds the adapter method, or the Harmony member in Members.cs). CAP n = capability map.
//
// Character state
//   isValid ................................. done: Exists(id)
//   isDead / isUnconcious ................... done: IsDead(id), IsUnconscious(id) (+ IsIncapacitated)              CAP 3
//   getName ................................. done: Name(id)
//   getPosition / getOrientation ............ done: Position(id), Yaw(id)                                           CAP 5
//   getFaction / isPlayerCharacter .......... done: Faction(id), FactionName(world, data, faction), IsPlayer(id)
//   getGameData (stringID) .................. done: RecordId(id)
//   getStats ................................ done: Stats(id) -> Core.Glue.CombatStats (STORED trained values)     CAP 10
//   getOverallSkillLevel_0_100 .............. done: OverallSkill(id) (spike S12 stand-in, Core.Glue.OverallSkill)
//   toughness() ............................. done: Stats(id).ScaledToughness (stat x Scale, CharacterStats.cs:63)
//   strengthActual / dexterityActual, getMeleeAttack_melee / getMeleeAttack_unarmed(true), getMeleeDefence_melee(false), getDodge(true),
//   getEquippedWeaponSkill .................. done (task 7.2): src/Game/ArenaCombatProfile.cs (Read: the internal CombatSystem ratings through
//       Members.CombatAttackRating / DefenceRating / UnarmedRating; the dodge, strengthActual / dexterityActual (Actuals) and skills from public
//       reads); the Arena window's card reads Actuals; task 8.1 reads the profile (bookie card). docs/divergences.md "Matchmaking snapshot ..."
//   getInventory, equipped items ............ done: Items(id), Equipped(id)                                        CAP 10
//   getMedical .............................. done (parts): LimbHealth(id), BloodRatio(id), AidNeed(id, robot); the rest under Medical
//   getMovement / getAI / getOrdersReciever . FightStarter's uses done (task 3.1, below); ArenaIngress done (task 4.3b): MoveTo, EndWalk,
//       MovementSpeed, MoveDestination; 7.3 (NPC bouts)                                                                CAP 5
//   getAttackTarget, isInCombatMode(true, false) ... done (task 3.1): AttackTarget(id), InMeleeCombatMode(id)          CAP 1
//   getAllAttackers ......................... done (task 6.2, prisoner protection): AttackersOf(id) (CombatSystem.TargetOf over the table);
//       other isInCombatMode uses ... 3.3 (KOWatcher/SparSession), 8.2 (spectators); CombatHooks (3.2b) needed none  CAP 1, 3
//   isChainedMode ........................... done (task 6.1): IsChained(id) (LockSystem.Chained)                    CAP 15
//   isBeingCarried .......................... done (task 3.4, the ring guard): IsCarried(id); isCarryingSomething ... done (task 6.3, the
//       cage return): IsCarrying(id); what is carried ... 8.4 (aftercare)                                              CAP 12
//   inSomething / inWhat (IN_BED, in a cage, ...) ... done for cages (task 6.1): src/Engine/Cages.cs IsImprisoned / HeldIn; 8.4 (IN_BED)   CAP 12, 15
//   getCurrentTownLocation .................. done: Towns.CurrentTownOf(world, id) (src/Engine/Towns.cs: the status town; task 7.2 review); "Scratch active" task 7.1   CAP 6
//   getHandle (live handles that go stale safely) ... done: CharacterId (slot + generation), every method here takes one
//   InstanceID::uid of a character/building . task 7.5 (saved planned-bout registry id, legacy keys)                   CAP 9.2
//   persistent per-character id: hooks Character::_NV_serialise / _NV_loadFromSerialise / _DESTRUCTOR (FighterIdentity.cpp:173-178)
//       ................................... done (task 2.3, no hook): PortraitSerial(id) + RecordId(id), AllCharacters, CharacterStamp;
//                                             src/Game/FighterIdentity.cs                                             CAP 9.2
//   race is robot (bed type, kit type) ..... done inside AidNeed; IsRobot(id) task 8.4
//   standing orders hold/passive get/set, movement speed orders get/set/restore ... done for spars (task 3.1): Orders(id),
//       SetCombatStance(id), RestoreStance(id, saved); town 7.3                                                     CAP 5
//   the Jobs toggle ......................... done (task 3.1): SetJobsOff(id), SetJobsOn(id); ArenaIngress (4.3b) needs no permanent-job read (Jobs off through FightStarter); 7.3   CAP 5
// Combat / hostility
//   rememberCharacter(ST_TEMPORARY_ENEMY | ST_TEMPORARY_ALLY), clearAllTempEnemyStatuses, clearTempEnemyStatus, attackTarget,
//   attackingYou, endCombatMode ............. done for spars (task 3.1): no temp-enemy memory (the senses tag fighters Enemy, CAP 1);
//       attackTarget + attackingYou -> Attack(id) both ways; endCombatMode -> EndTask(id) / Stop(ids); town pairs task 7.3   CAP 1, 2
//   hooks Character::_NV_isEnemy, Character::_NV_isAlly, CombatClass::isFightingAnAllyOfMine (opponents enemies, partners not allies,
//   squadmates stay out; prisoners allies of outsiders) ... done (task 3.2b): no patch needed for one faction (CAP 1, spike S1); the
//       prisoner/town isolation = src/Game/CombatHooks.cs BestAttackerOf / FindFoe postfixes over the published view; prisoner side task 6.2   CAP 1
//   hooks Character::attackTarget, Character::iShouldntAggravateThisTarget (outsiders never engage match prisoners / bout fighters)
//       ................................... done (task 3.2b): CombatHooks' BestAttackerOf / FindFoe postfixes (+ BeginPickUp prefix); the
//                                             prisoners (task 6.2) and town fighters (TownArena.CopyFighters) they isolate  CAP 1, 2
//   hook CombatClass::_iHitYouAreYouHit (soft-missed hits, stats, retarget), CombatClass::_getHit, getCurrentTechnique()->isDodge,
//   Damages::total ........................... done (task 3.2b): CombatHooks' ApplyBlow pre/postfix (stats, reaction) + the hit
//                                             observer's damage, paired; soft-miss net / _getHit / isDodge not needed (spike S1)   CAP 3, 4
//   hooks Character::_NV_hitByMeleeAttack, Character::attackingYou (town-bout incident context) ... done (task 7.4): not needed (the
//       relation prefix tests the blow's own pair; src/Town/TownCombatRules.cs)                                        CAP 2
//   crime / relation / campaign suppression for sanctioned pairs: hooks BountyManager::setCrime, Character::notifyTheCampaignOfAnAttack,
//   FactionRelations::_NV_affectRelations, FactionRelations::_NV_setEnemy (CombatHooks.cpp:274-358; town predicates TownArena.cpp:1415-1456)
//       ................................... done (task 7.4): CrimeSystem.SetPending and FactionDeeds.Blow prefixes over the view's
//                                             IsSanctionedPair (src/Game/CombatHooks.cs); no campaign notice in Meitou; TEMP_ENEMY after a town
//                                             bout: FactionSquads, HoldsTempEnemy(squad, target), ClearTempEnemy; same-faction spars need none   CAP 2, 20
// AI / orders / movement
//   addOrder(dest, TaskType, subject, shift, clear, pos) with FOCUSED_MELEE_ATTACK ... done: Attack(id, target, player, queued) (task 3.1);
//       FightStarter's MOVE_CUS_ORDERED queue overwrite -> EndTask (3.1); MOVE_CUS_ORDERED done for ArenaIngress (task 4.3b: MoveTo, one
//       MoveOrder per character id); 7.3 / 8.2;
//       RELEASE_PRISONER task 6.2: not given (divergences.md "PrisonerUtil (task 6.2"); LOCK_DOOR task 6.3: not given (Imprison locks, divergences.md "PrisonerUtil (task 6.3"); LIFT_PERSON_PLAYER_ORDER done (task 6.3): BeginLift(id, body); FIRST_AID_ORDER / FIRST_AID_ROBOT task 4.7 / 8.4;
//       PUT_SOMEONE_IN_BED, USE_BED_ORDER task 8.4; FEED_CORPSE_INTO_MACHINE task 8.6 (spike S9); a seat's default task
//       (UseableStuff::getDefaultTask) task 8.2                                                                      CAP 5, 12, 14, 15, 19
//   removeJob(FOCUSED_MELEE_ATTACK | UNPROVOKED_FOCUSED_MELEE_ATTACK) ... done: EndTask(id) (task 3.1)
//   removeJob(MOVE_CUS_ORDERED) ............. done (task 4.3b): EndWalk(id) (a GoTo walk ended)
//   removeJob(FOCUSED_MELEE_ATTACK | UNPROVOKED_...) / endCombatMode on a prisoner's attacker ... done (task 6.2): EndAttack(id), EndCombat(id)
//       (CombatSystem.Disengage by reflection, Members.CombatDisengage);
//       PrisonerUtil's ClearHandlerOrderQueue (a queue-replacing move, then removeJob) ... done (task 6.2): ClearOrders(id)
//   checkPlayerOrderForProblems, other removeJob(task) ... task 8.4
//   addGoal(UNPROVOKED_FOCUSED_MELEE_ATTACK), clearAllAIGoals, reThinkCurrentAIAction (FightStarter's NPC fight) ... done: Attack(id, ...,
//       player false) / EndTask(id) (task 3.1); ArenaIngress's park of an NPC (addGoal(STAND_STILL), clearAllAIGoals: task 4.3b, HoldStill /
//       ReassertHold, Engaged); the rest tasks 7.3, 8.2
//   NPC AI ownership (hook AITaskSytem::checkOrders by export, clearOrders, chooseGoal, callForUpdate, tryToGetCurrentGoal: suppress an
//   NPC's own job choice while the mod drives it, then hand it back) ... Teams 1v1 bench done (task 3.3): HoldStill(id), ReassertHold(id),
//       ReleaseHold(id) (CharacterCold.Engaged, spike S10); town bouts task 7.3                                       CAP 5, 19
//   setStandingOrder / getStandingOrder (PASSIVE, HOLD, DEF, AGG) ... done for spars (task 3.1, above), the bench SetBenchStance(id, hold)
//       (task 3.3), the staging (task 4.3b: park = SetBenchStance(id, true), a move = SetMoveStance(id)); town 7.3                                                                                          CAP 5
//   getMovementSpeedOrders, setDesiredSpeedOrders, setDesiredSpeed(RUN), restoreDesiredSpeed ... PrisonerUtil's IssueRun done (task 6.2):
//       SetRunSpeed(id) (CharacterCold.RunSpeed = SpeedMode.Free, Kenshi's RUN); ArenaIngress uses RUN only for a town bout
//       fighter (ArenaIngress.cpp:448-452, :461-463): task 7.3; 8.4
//   CharMovement::setDestination(pos, HIGH_PRIORITY, true), Character::setDestination, destination, isCurrentlyMoving ... the bench done
//       (task 3.3): MoveTo(id, pos) (MoveOrder), MoveDestination(id); ArenaIngress done (task 4.3b, same methods; getMovementSpeed ->
//       MovementSpeed(id)); tasks 7.3, 8.2, 8.4
//   combatMover.setForcedWP / hasForcedWP, _setPositionSimple ... done (task 3.4): TryPlaceGroundedKeepingTask(id, x, nearY, z) (CharacterTable.Place keeping
//       yaw and task; no forced waypoint in Meitou, so the near-band steer is a small step, divergences.md); the town bouts reuse it (7.3)   CAP 5
//   lookatPosition (face the arena / the trader) ... done (task 3.3): Face(id, pos) (CharacterHot.Yaw); used by 4.3b (done), 8.2, 8.5   CAP 5
//   say(text) ............................... done: Bark(id, text) (countdown done, task 4.3b; announcer 8.3, crowd 8.2, limb shop 8.5)   CAP 16
//   dropCarriedObject ....................... done (task 6.3): LetGo(id) (CarrySystem.LetGo); 8.4                          CAP 12
//   Jobs toggle via PlayerInterface selection + OrdersPanel::update + MainBarGUI::pressedKey(26) ... done: SetJobsOff / SetJobsOn (task 3.1;
//       Meitou's per-character job switch set directly, divergences.md)
//   hook Character::addOrder (swallow "use" on PG buildings; cancel the approach on a new order) ... done (task 4.2):
//       PlayerOrders.Give prefix in src/Game/ContextMenuHooks.cs                                                      CAP 5, 6
// Prison
//   setPrisonMode(on, cage), setChainedMode(on, hand), UseableStuff::getOccupant, getDoorLock, isBroken, destroyed,
//   Building::getSpecialFunction() == BF_CAGE, findAllFurnitureWithFunction, Ownerships::getHomeFurnitureOfType / getBuildingsWithFunction /
//   isOwned, AI::isDoorLocked(hand, pos), PlayerInterface::recruit(character, false) ... discovery done (task 6.1): src/Engine/Cages.cs
//       (getOccupant, BF_CAGE, findAllFurnitureWithFunction, Ownerships, its own E1 list); recruit 6.4; setPrisonMode(false) done (task 6.2):
//       Cages.Release (Fixtures.Release, releaser -1); setChainedMode(false): nothing (divergences.md "PrisonerUtil (task 6.2"); task 6.3:
//       setPrisonMode(true, cage) ... Cages.PutInCage (Fixtures.Imprison, which locks the cage); setChainedMode(true): nothing; getDoorLock,
//       isBroken, destroyed, AI::isDoorLocked ... Cages.HasLock / IsBroken / IsDestroyed / IsLocked (divergences.md "PrisonerUtil (task 6.3")                                                                       CAP 15
// Medical
//   anatomy[i]->flesh, fleshStun, maxHealth() done: LimbHealth(id); bandaging, isDead, isRobotic per part ... tasks 4.7, 8.4   CAP 12, 13
//   blood, getMaxBlood ...................... done: BloodRatio(id) (blood / capacity)
//   wounds.size(), currentBleedRate ......... done: WoundCount(id), BleedRate(id), HasMedical(id) (task 4.7)
//   scoreFirstAidNeed(robot) ................ done: AidNeed(id, robot)
//   blood-loss trauma, probably dying, crippled, has an arm to fight with, limb state per arm/leg (LIMB_STUMP, LIMB_REPLACED)
//       ................................... done for who may fight (task 7.2): MedicallyAble(id) (isProbablyDying: none); 8.4 (aftercare), 8.5 (limb shop)   CAP 12, 13
//   hooks MedicalSystem::applyFirstAid, applyDoctoring, applyRigging: the block done (task 4.7, src/Game/ArenaMedical.cs: one prefix on
//       FirstAidSystem.Apply); block town bystanders done (task 7.4, ArenaMedicalLogic.BlockFor); record progress, restock town medics ... task 8.4   CAP 12
//   Character::addOrder(FIRST_AID_ORDER | FIRST_AID_ROBOT, target) ... done: StartTreat(medic, patient, robot) (task 4.7)
//   hooks Character::shouldIHelpThisGuy, Character::pickupObject (stop bystanders treating/carrying fighters) ... done (task 7.4): FindPatient
//       postfix, BeginPickUp prefix (src/Game/CombatHooks.cs); IsScratchMedicActor's stringID ... RecordIdAt(slot)   CAP 12
//   beds: getSpecialFunction BF_BED / BF_SKELETON_BED, furnitureParentBuilding, UseableStuff occupant, numOperatorsMax, isDisabled,
//   stopOperating, currentOperators; hook Character::setBedMode (enter) and setBedMode(false) (leave) ... task 8.4   CAP 12
//   seats: BF_CHAIR, isFreeSlot, couldIOperate, occupant ... task 8.2 (spectators)                                   CAP 19
//   furnace: BF_CORPSE_DISPOSAL ............. task 8.6 (corpse cleanup)                                              CAP 14
// Items / inventory
//   ou->gamedata.getData(stringID), getDataByName(name, WEAPON_MANUFACTURER), GameData::getReferenceListIfExists("material" | "weapon models"),
//   findInList / addToList("weapon types"), RootObjectFactory::createItem, new Sword(...), Gear::level_0_100 / level, Item::getLevel,
//   Character::giveItem, hasRoomForItem, removeItemDontDestroy_returnsItem, ou->destroy(item) ... reward delivery done (task 5.2a):
//       src/Engine/Items.cs (its own E1 list; EnsureWeaponListed dropped, divergences.md "Reward items"); kits task 8.4;
//       limbs task 8.5                                                                                                CAP 10
//   Inventory::getAllSections, getItems, getItem, getAllItems, getEquippedArmour, getEquippedWeapons ... done: Items(id), Equipped(id)
//   getPrimaryWeapon, getSecondaryWeapon (+ weapon category) ... done (task 7.2): Fighter.BackWeapon / HipWeapon in src/Game/ArenaCombatProfile.cs   CAP 10
//   getAllItemsWithFunction, best first-aid / robot-repair item and its charges, Item::resetCharges, chargesLeft, itemFunction
//       ................................... tasks 4.7, 8.4 (medic kits: give or recharge, ArenaMedical.cpp:135-155)  CAP 10, 12
//   hasABackpackOn .......................... task 8.4 (ArenaMedical.cpp:112, the medic kits; RewardDelivery.cpp does not call it)
//   MedicalSystem::setRobotLimbItem (install a limb) ... task 8.5                                                     CAP 13
//   ou->activeMods (detect Extra Inventory Sections) ... not needed: no such plugin in Meitou (spike S13, divergences.md)
// World queries
//   getObjectsWithinSphere(origin, r, BUILDING, max) ... done: BuildingsNear(world, data, pos, r, recordIds) (town + player buildings)   CAP 6
//       level placements OUTSIDE every town: not built (divergences.md; they sit in the host's NavSystem, GameHost.Nav internal).
//   getObjectsWithinSphere(CHARACTER) / getCharactersWithinSphere ... done: CharactersNear(world, pos, r)
//   (the query methods return new lists; fill-a-caller's-list overloads come with the first per-tick caller, tasks 8.2 / 8.4)
//   Building::getPosition, getOrientation ... done: PgBuilding.Position / Yaw
//   getUseableStuff()+0x400 (the use node's functionality record, ArenaIdentity.cpp:73-84) ... done (task 4.1): FunctionRecord(production,
//       record) = ProductionSystem.Data(record)?.Functionality, the BUILDING record's first functionality (null for a building that neither
//       works nor stores: then the building record is the only match, as for Arena 183 / 334). Shipped PG function records: 47 (Registry 45
//       and Registry Town 195), 60 (Banner 42), 57 (Leaderboards 53), 273 (Leaderboards Town 272)                            CAP 6
//   getUseableStuff (seats, beds) ........... tasks 8.2, 8.4                                                          CAP 6
//   Building::getPositionMarker(from) ........ done (task 4.2): PositionMarker(world, db, building) = WorldObjectLayout.PositionMarker
//       (WorldObjectLayout.cs:434, the branch of a building with fewer than two use nodes, as Fixture.Marker uses it, FixtureRegistry.cs:763)
//       for RegistryApproachPoint (ArenaIngress.cpp:490)                                                               CAP 6
//   ArenaIdentity's buildings (task 4.1) ..... done: BuildingsNear + FindBuilding(source, key, hint) (re-find a held building by its key;
//       both use IsListed / FromTown / FromPlayer),
//       src/Game/ArenaIdentity.cs over Core/Glue/ArenaIdentityLogic.cs                                         CAP 6
//   getBuildState()->isComplete ............. done: PgBuilding.Complete
//   isThePlayer ............................. done: PgBuilding.Source == Player
//   Building::getTown ....................... done: PgBuilding.Town, Towns.SameTown(building, character)            CAP 6
//   InstanceID::uid of a building (saved planned-bout registry id) ... done: PgBuilding.Key; saved by task 7.5
//   Faction::getAllActiveSquads, Platoon::getActivePlatoon()->getThings() ... done: PlayerSquads(world)
//   getAllPlayerCharacters .................. done: PlayerCharacters(world)
//   PlayerInterface::selectedCharacters ..... done (task 4.2): SelectedCharacters(world) = world.Player.Selection (PlayerState.cs:91, world
//       state changed by SelectCommand), for src/Game/SquadUtil.cs
//   ou->isPaused ............................ not needed: a paused world runs no SlowWorld (World.Interface.cs:16-40)
//   per-frame tick, real-time clock (GetTickCount, skipped while paused) ... done in task 2.1 (SlowWorld, PgClock)
//   positions, distances, building origin as the arena centre ... done: Position(id), PgBuilding.Position; arena centre (task 4.1):
//       ArenaBuilding.Position (the building's origin, ArenaRingGuard.cpp:82); FindSite (ArenaIngress.cpp:175-233) in src/Game/ArenaIdentity.cs
//   a session key for player characters ... done: src/Game/FighterIdentity.cs (task 2.3)                              CAP 9.2
//   total in-game hours (getTimeStamp_inGameHours().getTotalHours()) ... task 2.4 (world.GameHours; card epoch, start times, cooldown
//       and restore window read it in 7.5 / 8.1)                                                                      CAP 18
// UI
//   ou->showPlayerAMessage(text, true) ...... done: Message(world, text)                                             CAP 16
//   MyGUI widgets (Window, ScrollView, Button, EditBox, ImageBox, TextBox, TabControl), PortraitManager::setImageWidget(handle, image,
//   true), ForgottenGUI::inDialogue / selectedObject / selectedPlayerCharacter / currentCursor / mainbar->moneyText, moneyLabel
//       ................................... tasks 4.4-4.6, 5.3, 6.4, 7.6, 8.1                                       CAP 7
//   open the registry window from the building (the shared approach flow, ArenaIngress.cpp:955-990) ... done (task 4.2), 7.6 (town)
//   GetAsyncKeyState(VK_F8 / VK_ESCAPE), InputHandler key->mWheel / escape ... task 2.6 ("Debug menu key" setting), 4.4 (Esc, wheel)   CAP 7.10
//   hooks ContextMenu::showContextMenu, Building::_NV_getMouseCursor, ForgottenGUI::changeMouseCursor (2), Building::_NV_getDefaultTask,
//       Character::addOrder ... done (task 4.2): src/Game/ContextMenuHooks.cs (PlayerInterface.PickedRightDown prefix, spike S3;
//       PointerView.PickWithSelection postfix; PlayerOrders.Give prefix; the menu-open watch)                           CAP 5, 6
//   hook ForgottenGUI::showTradeWindow (money trade with Null -> Rewards, with the bookie -> Bookie) ... done for Null (task 5.3a: the S4
//       DialogueSystem.ConversationEffect prefix in src/Game/ContextMenuHooks.cs; InDialogue for ForgottenGUI::inDialogue); the bookie: done (task 8.1a)   CAP 6, 17
//   hook ForgottenGUI::update (the per-frame loop) ... done in task 2.1 (ProvingGroundsSystem.SlowWorld)
//   hooks TitleScreen::_CONSTRUCTOR (create panels), OptionsWindow::_NV_update (PG CONFIG button) ... tasks 4.4, 2.6 (Meitou mod settings)  CAP 7, 8
// Save
//   GameSaveState::getState(GAMESTATE_CHARACTER), GameData::sdata + addString, GameDataContainer::_getAllData (CAMERA record),
//   SaveFileSystem::state / isRunning / currentSave / failedToCopyError, SaveManager::signal / delay / pauseAutoSaveTimer;
//   hooks SaveManager::saveGame / loadGame / importGame, GameDataContainer::save / load, SaveFileSystem::saveGame / loadGame / sync,
//   SaveManager::updateAutoSave / save / execute ... done (task 2.4, no hook): IModSaveData on ProvingGroundsSystem ->
//       src/Game/ArenaNativePersistence.cs (inside the save; no sidecar, no generation metadata); save safety (updateAutoSave / save /
//       execute) task 2.5                                                                                             CAP 9
//   active save key (seeds the challenge card; LeaderboardStore::HasActiveSave gates the town schedule) ... task 2.4: the snapshot's
//       saveKey is the fixed ArenaSaveData.InSaveKey (Meitou matches nothing); 7.5 (card seed), 7.1 (schedule gate)   CAP 9
//   read/write a mod sidecar with the card, credits and planned bout ... done (task 2.4): one IModSaveData entry (ArenaSaveData layout)   CAP 9.1
//   new game vs loaded world (WorldLifecycle.cpp:216-225 NEWGAME dispatch) ... done: LoadedFromSave(world) (World.Saved)
//   arena ledger ready / blocked (LeaderboardStore::HasActiveSave, g_ledger.Ready()) ... done (task 2.4): ArenaNativePersistence.Ready; M3 (StopWithStatus),
//       5.1 (LeaderboardStore), M7 (town schedule) MUST check it before any arena read or write, as the C++ does
//   save/load life cycle: before a save cancel or abort bouts and suspend limb visits (WorldLifecycle.cpp:83-117, IsArenaOperationBusy),
//   on world teardown drop every handle (hook GameWorld::_clearAndDestroyGameWorldStuff, WorldLifecycle.cpp:267) ... task 2.5   CAP 9
// Money (TW §3)
//   player faction's money get / take / add (Faction::factionOwnerships, TownChallengeWallet.cpp:11-31) ... tasks 5.2, 7.5, 8.1, 8.5
//       (world.Player.Money)                                                                                          CAP 10
// Town fighter staging, countdown, bout (ArenaIngress, SparSession reused by the town) ... staging + countdown done (task 4.3b,
//   src/Game/ArenaIngress.cs); the town's own inputs task 7.3

/// <summary>
/// The engine adapter: one method per original engine call (header above). Reads and writes are <b>sim thread, serial steps only</b>
/// (<c>Inputs</c>, <c>SlowWorld</c>); the frame thread never calls this class (it reads the published snapshot).
/// <para>Character ids are Meitou's <see cref="CharacterId"/> (slot + generation). A stale, removed or out-of-range id never throws: a read
/// returns its documented "no value" (null, false, NaN, empty, <see cref="CombatStats.None"/>), a write does nothing and returns false.
/// A slot holds a character while it lives AND after it died until its corpse is removed (<c>CharacterHot.Alive</c> = the slot is used,
/// CharacterTable.cs:24-27), so a dead fighter keeps answering <see cref="IsDead"/> true.</para>
/// <para>Ids resolve against the state being computed, <c>CharacterTable.Next</c>, with <c>TryResolveNext</c> (CharacterTable.cs:1414-1418),
/// never <c>IsAlive</c> (:1408-1409), which reads the LAST tick's buffer: the cold half (<c>Cold(slot)</c>, :1398) and <c>Next</c> are this
/// tick's, so a slot freed and spawned again within the tick (Remove :1508-1518, Spawn :1462-1497) would answer an old id with the new
/// character, and a character spawned this tick would read as absent. This is valid only INSIDE a tick (RunTick, World.cs:608-634:
/// BeginTick copies Previous into Next, :1441-1446; EndTick swaps them, :1452-1456), which is where Inputs and SlowWorld run; outside a tick
/// <c>Next</c> is the swapped-out old buffer. The adapter is called only from the system's Inputs and SlowWorld.</para>
/// </summary>
static class PgEngine
{
    // ---------- character state ----------

    /// <summary>The character's cold half this tick, or null (stale id): <c>TryResolveNext</c> (CharacterTable.cs:1414-1418), then
    /// <c>Cold(slot)</c> (:1398). See the class summary for why not <c>IsAlive</c>.</summary>
    static CharacterCold? Cold(World world, CharacterId id) =>
        world.Characters.TryResolveNext(id, out int slot) ? world.Characters.Cold(slot) : null;

    /// <summary>C++ <c>isValid()</c>: the id still names a character this tick (alive or a corpse).</summary>
    internal static bool Exists(World world, CharacterId id) => world.Characters.TryResolveNext(id, out _);

    /// <summary>C++ <c>isDead()</c>: <c>MedicalState.Dead</c> (MedicalState.cs:86). False for a stale id (ask <see cref="Exists"/>).</summary>
    internal static bool IsDead(World world, CharacterId id) => Cold(world, id)?.Medical is { Dead: true };

    /// <summary>C++ <c>isUnconcious()</c>: <c>MedicalState.Unconscious</c> (MedicalState.cs:81), which stays true while playing dead
    /// (:216-221, CAP 3). False for a stale id.</summary>
    internal static bool IsUnconscious(World world, CharacterId id) => Cold(world, id)?.Medical is { Unconscious: true };

    /// <summary>Knocked out or dead, <c>MedicalState.Incapacitated</c> (MedicalState.cs:110). False for a stale id.</summary>
    internal static bool IsIncapacitated(World world, CharacterId id) => Cold(world, id)?.Medical is { Incapacitated: true };

    /// <summary>C++ <c>getName()</c>: <c>CharacterCold.Name</c> (CharacterTable.cs:114). Null for a stale id.</summary>
    internal static string? Name(World world, CharacterId id) => Cold(world, id)?.Name;

    /// <summary>C++ <c>getGameData()-&gt;stringID</c>: <c>CharacterCold.RecordId</c> (CharacterTable.cs:133). Null for a stale id.</summary>
    internal static string? RecordId(World world, CharacterId id) => Cold(world, id)?.RecordId;

    /// <summary>The number that turns the character's portrait camera, <c>CharacterCold.PortraitSerial</c> (CharacterTable.cs:670, set once at
    /// spawn with <c>??=</c>, :1488; saved as <c>portrait_serial</c>, SaveCapture.cs:805, read back at load, SavedPopulation.cs:548-551). The
    /// fighter identity's key (task 2.3, CAP 9.2). Null for a stale id or none set.</summary>
    internal static uint? PortraitSerial(World world, CharacterId id) => Cold(world, id)?.PortraitSerial;

    /// <summary>The tick the character was spawned at in this world, <c>CharacterCold.SpawnedTick</c> (CharacterTable.cs:128, set at Spawn,
    /// :1487). -1 for a stale id.</summary>
    internal static long SpawnedTick(World world, CharacterId id) => Cold(world, id)?.SpawnedTick ?? -1;

    /// <summary>Whether the character came from the loaded save: <c>CharacterCold.Save</c> non-null (CharacterTable.cs:526-531, "null for a
    /// character the game made itself"; set by SavedPopulation.cs:501). False for a stale id.</summary>
    internal static bool FromSave(World world, CharacterId id) => Cold(world, id)?.Save is not null;

    /// <summary>Every slot used this tick (<c>Next[slot].Alive</c>) with a cold half, alive or a corpse, in slot order; ids from
    /// <c>Next[slot].Generation</c> (as <see cref="PlayerCharacters"/>). For the fighter identity's scan (task 2.3).</summary>
    internal static List<CharacterId> AllCharacters(World world)
    {
        var table = world.Characters;
        var next = table.Next;
        var list = new List<CharacterId>(table.Count);
        for (int slot = 0; slot < next.Length; slot++)
            if (next[slot].Alive && table.Cold(slot) is not null) list.Add(new CharacterId(slot, next[slot].Generation));
        return list;
    }

    /// <summary>Port only (task 10.1, the interop API's town fighter roster): every living character of faction <paramref name="faction"/> (a
    /// position in PopulationData.Factions) whose record id passes <paramref name="keep"/>, with its shown name (CharacterCold.Name), appended;
    /// one pass over the table as <see cref="AllCharacters"/>, no allocation besides the list's growth.</summary>
    internal static void FactionCharacters(World world, int faction, Func<string?, bool> keep, List<(CharacterId Id, string Name)> into)
    {
        if (faction < 0) return;
        var table = world.Characters;
        var next = table.Next;
        for (int slot = 0; slot < next.Length; slot++)
        {
            if (!next[slot].Alive || table.Cold(slot) is not { } cold) continue;
            if (cold.Faction != faction || cold.Medical is { Dead: true } || !keep(cold.RecordId)) continue;
            into.Add((new CharacterId(slot, next[slot].Generation), cold.Name ?? ""));
        }
    }

    /// <summary>A cheap stamp of the character table: <c>Spawns</c> (CharacterTable.cs:1383, +1 on every Spawn, :1494) and <c>Count</c>
    /// (:1378 = HighWater - free slots; every Remove frees a slot, :1516). A change in the set of live characters always moves one of
    /// them (a slot freed and reused within a tick still bumps Spawns).</summary>
    internal static (long Spawns, int Count) CharacterStamp(World world) => (world.Characters.Spawns, world.Characters.Count);

    /// <summary>C++ <c>getPosition()</c>: <c>world.Characters.Next[slot].Position</c> (CharacterTable.cs:43-46, :1393; CAP 5). Null for a stale id.</summary>
    internal static Vector3? Position(World world, CharacterId id) =>
        world.Characters.TryResolveNext(id, out int slot) ? world.Characters.Next[slot].Position : null;

    /// <summary>C++ <c>getOrientation()</c> as a heading: <c>Next[slot].Yaw</c>, radians, 0 along +Z turning towards +X (CharacterTable.cs:48-52).
    /// NaN for a stale id.</summary>
    internal static float Yaw(World world, CharacterId id) =>
        world.Characters.TryResolveNext(id, out int slot) ? world.Characters.Next[slot].Yaw : float.NaN;

    /// <summary>C++ <c>getFaction()</c>: <c>CharacterCold.Faction</c>, a position in <c>PopulationData.Factions</c> (CharacterTable.cs:118;
    /// may itself be -1 = none). Null for a stale id.</summary>
    internal static int? Faction(World world, CharacterId id) => Cold(world, id)?.Faction;

    /// <summary>A faction's shown name, as the host names it (GameHost.cs:1080-1086 FactionNameOf: the player's own renamed name, else
    /// <c>PopulationData.Factions[f].Name</c>). Null for no faction (null or out of range).</summary>
    internal static string? FactionName(World world, PopulationData data, int? faction)
    {
        if (faction is null) return null;
        if (faction >= 0 && faction == world.Player.Faction && world.Player.FactionName is { } renamed) return renamed;
        return faction >= 0 && faction < data.Factions.Count ? data.Factions[faction.Value].Name : null;
    }

    /// <summary>C++ <c>isPlayerCharacter()</c>: <c>CharacterCold.IsPlayer</c> (CharacterTable.cs:287). False for a stale id.</summary>
    internal static bool IsPlayer(World world, CharacterId id) => Cold(world, id)?.IsPlayer == true;

    /// <summary>C++ <c>getStats()</c>: the STORED (trained) levels, <c>CharacterCold.Stats[StatsEnumerated.X]</c> (CharacterTable.cs:380,
    /// CharacterStats.cs:34-38) and <c>Scale</c> (:40-45). <see cref="CombatStats.None"/> for a stale id or a character without stats.</summary>
    internal static CombatStats Stats(World world, CharacterId id)
    {
        if (Cold(world, id)?.Stats is not { } s) return CombatStats.None;
        return new CombatStats
        {
            Valid = true,
            MeleeAttack = s[StatsEnumerated.MeleeAttack], MeleeDefence = s[StatsEnumerated.MeleeDefence], Dodge = s[StatsEnumerated.Dodge],
            Strength = s[StatsEnumerated.Strength], Toughness = s[StatsEnumerated.Toughness], Dexterity = s[StatsEnumerated.Dexterity],
            Katanas = s[StatsEnumerated.Katanas], Sabres = s[StatsEnumerated.Sabres], Blunt = s[StatsEnumerated.Blunt],
            HeavyWeapons = s[StatsEnumerated.HeavyWeapons], Hackers = s[StatsEnumerated.Hackers], MartialArts = s[StatsEnumerated.MartialArts],
            Crossbows = s[StatsEnumerated.Crossbows], Turrets = s[StatsEnumerated.Turrets], Polearms = s[StatsEnumerated.Polearms],
            Scale = s.Scale,
        };
    }

    /// <summary>C++ <c>getStats()-&gt;getOverallSkillLevel_0_100()</c>: spike S12's stand-in over <see cref="Stats"/>, a float in [0, 100], not
    /// rounded. NaN for a stale id or no stats.</summary>
    internal static float OverallSkill(World world, CharacterId id) => Core.Glue.OverallSkill.Of(Stats(world, id));

    // ---------- medical (CAP 12, 13) ----------

    /// <summary>One body part as the original reads <c>MedicalSystem::anatomy[i]</c>: <c>flesh</c> = <c>HealthPart.Flesh</c>, <c>fleshStun</c> =
    /// <c>Stun</c>, <c>maxHealth()</c> = <c>MaxHp</c> (src/Meitou.Simulation/Bodies/HealthPart.cs:53, :58, :140); Name is the part template's.</summary>
    internal readonly record struct PgPart(string Name, float Flesh, float Stun, float MaxHp);

    /// <summary>Every body part in <c>MedicalState.Parts</c> order (MedicalState.cs:42). Empty for a stale id or no medical state.</summary>
    internal static PgPart[] LimbHealth(World world, CharacterId id)
    {
        if (Cold(world, id)?.Medical is not { } med) return [];
        var parts = new PgPart[med.Parts.Count];
        for (int i = 0; i < parts.Length; i++)
        {
            var p = med.Parts[i];
            parts[i] = new PgPart(p.Template.Name, p.Flesh, p.Stun, p.MaxHp);
        }
        return parts;
    }

    /// <summary>The blood capacity Meitou's own readers use (Ai/SquadIntact.cs:55, WorldPublish.cs:879): <c>BloodCapacity(race, strength (50
    /// without stats), HasStatsObject, ScaleK, ScaleZ)</c> (MedicalState.cs:352-359), at least 1. NaN without a race or medical state.</summary>
    static float Capacity(CharacterCold c) =>
        c.Race is { } race && c.Medical is { } med
            ? MathF.Max(MedicalState.BloodCapacity(race, c.Stats?.Strength ?? 50, med.HasStatsObject, med.ScaleK, med.ScaleZ), 1)
            : float.NaN;

    /// <summary>C++ <c>blood / getMaxBlood()</c>: <c>MedicalState.Blood</c> (MedicalState.cs:56) / the capacity (as the HUD body status,
    /// WorldPublish.cs:885). NaN for a stale id, no race or no medical state.</summary>
    internal static float BloodRatio(World world, CharacterId id) =>
        Cold(world, id) is { Medical: { } med } c ? med.Blood / Capacity(c) : float.NaN;

    /// <summary>C++ <c>MedicalSystem::scoreFirstAidNeed(robot)</c>: <c>MedicalState.FirstAidNeeds(race, capacity, extraLoss)</c>
    /// (MedicalState.Tick.cs:118-140; -0.1 for a dead patient), the organic or the robot score, with <c>extra blood loss from bodyparts</c>
    /// from the world's <see cref="BodySystem"/> constants (1 without one, as Ai/SquadIntact.cs:56). NaN for a stale id or no race.</summary>
    internal static float AidNeed(World world, CharacterId id, bool robot)
    {
        if (Cold(world, id) is not { Race: { } race, Medical: { } med } c) return float.NaN;
        var (organic, robotScore) = med.FirstAidNeeds(race, Capacity(c), ExtraBloodLoss(world));
        return robot ? robotScore : organic;
    }

    /// <summary>C++ <c>getMedical() != NULL</c>: the character has a <c>CharacterCold.Medical</c> state. False for a stale id. (Task 4.7.)</summary>
    internal static bool HasMedical(World world, CharacterId id) => Cold(world, id)?.Medical is not null;

    /// <summary>C++ <c>RosterStatus::Read(c, health, reason)</c> (RosterStatus.cpp:9-48) over <see cref="LimbHealth"/> and <see cref="BloodRatio"/>
    /// (Core/RosterStatus.cs decides; <paramref name="error"/> is its reason text). Task 7.2 (town readiness).</summary>
    internal static bool RosterHealth(World world, CharacterId id, out RosterStatus.Snapshot health, out string error)
    {
        var parts = LimbHealth(world, id);
        float ratio = BloodRatio(world, id);
        var anatomy = new RosterStatus.Part?[parts.Length];
        for (int i = 0; i < parts.Length; i++) anatomy[i] = new RosterStatus.Part(parts[i].Flesh, parts[i].Stun, parts[i].MaxHp);
        bool finite = float.IsFinite(ratio);
        return RosterStatus.Read(Exists(world, id), HasMedical(world, id), finite ? 1f : 0f, finite ? ratio : 0f, anatomy, out health, out error);
    }

    /// <summary>C++ <c>isInCombatMode(true, true)</c> (TownArena.cpp:318): in a melee or a ranged fight, <c>CharacterCold.InCombat</c>
    /// (CharacterTable.cs:546, "the character is in a fight (set by the combat system)"). Meitou's own <c>CombatSystem.IsInCombatMode</c>
    /// (CombatSystem.Hud.cs:68, internal) also asks the ranged controller's target to be up; the fight flag alone is used here
    /// (docs/divergences.md "Matchmaking snapshot and search worker"). False for a stale id. Task 7.2.</summary>
    internal static bool InCombatMode(World world, CharacterId id) => Cold(world, id) is { InCombat: true };

    /// <summary>C++ <c>!med-&gt;isInBloodlossTrauma() &amp;&amp; !med-&gt;isProbablyDying() &amp;&amp; !med-&gt;isCrippled() &amp;&amp; med-&gt;hasAnArmToFightWith()</c>
    /// (TownArena.cpp:323-324, a medic's native capacity): <c>MedicalState.BloodKo</c> (MedicalState.cs:93-98, "+0x163 bloodlossTrauma"),
    /// <c>Crawling</c> (Meitou's crippled flag, docs/game/combat.md "crippled flag"), either arm working (<c>ArmWorks</c>, MedicalState.cs:365-383,
    /// "an arm to fight with" in docs/game/combat.md: either arm usable). <c>isProbablyDying</c> has no Meitou counterpart and reads false
    /// (docs/divergences.md). False for a stale id or no medical state. Task 7.2.</summary>
    internal static bool MedicallyAble(World world, CharacterId id) =>
        Cold(world, id)?.Medical is { } med && !med.BloodKo && !med.Crawling && (med.ArmWorks(left: true) || med.ArmWorks(left: false));

    /// <summary>C++ <c>MedicalSystem::wounds.size()</c>: <c>MedicalState.Wounds.Count</c> (MedicalState.cs:284, the open wounds first aid closes
    /// first, FirstAidSystem.cs:555-559). 0 for a stale id or no medical state. (Task 4.7.)</summary>
    internal static int WoundCount(World world, CharacterId id) => Cold(world, id)?.Medical?.Wounds.Count ?? 0;

    /// <summary>C++ <c>MedicalSystem::currentBleedRate</c>: <c>MedicalState.Bleeding</c> (MedicalState.cs:61, set each body tick,
    /// MedicalState.Tick.cs:459). 0 for a stale id or no medical state. (Task 4.7.)</summary>
    internal static float BleedRate(World world, CharacterId id) => Cold(world, id)?.Medical?.Bleeding ?? 0f;

    /// <summary>Test output (task 4.7, pg probe "bandage"): the sum of <c>HealthPart.Bandage</c> (HealthPart.cs:63) over the parts, the HP first aid
    /// has bandaged. NaN for a stale id or no medical state.</summary>
    internal static float BandageTotal(World world, CharacterId id)
    {
        if (Cold(world, id)?.Medical is not { } med) return float.NaN;
        float total = 0;
        foreach (var p in med.Parts) total += p.Bandage;
        return total;
    }

    /// <summary>C++ <c>medic-&gt;addOrder(NULL, robot ? FIRST_AID_ROBOT : FIRST_AID_ORDER, target, false, true, pos)</c> (ArenaMedical.cpp:259-266):
    /// CAP 12's code route, <c>TaskStart.Start(world, medic, new TaskRequest(CharacterTask.Treat, TaskType.FirstAidOrder | FirstAidRobot,
    /// TaskSource.Goal) { Target = patient, Resume = TaskStart.ResumeOf(world, medic, Treat, Idle) })</c> (TaskStart.cs:285, :422; the treat
    /// handler FirstAidSystem.cs:271 -> Begin :300, as FirstAidSystem's own order start does, :240-244; rows 25 and 60, TaskType.cs:114, :254; the
    /// handler treats with whichever kit the patient's parts need, FirstAidSystem.cs:555-583). False for a stale id or a refused start.
    /// Serial step.</summary>
    internal static bool StartTreat(World world, CharacterId medic, CharacterId patient, bool robot)
    {
        if (!world.Characters.TryResolveNext(medic, out int slot) || !world.Characters.TryResolveNext(patient, out _)) return false;
        var row = robot ? Meitou.Data.Gameplay.Ai.TaskType.FirstAidRobot : Meitou.Data.Gameplay.Ai.TaskType.FirstAidOrder;
        return TaskStart.Start(world, slot, new TaskRequest(CharacterTask.Treat, row, TaskSource.Goal)
        {
            Target = patient,
            Resume = TaskStart.ResumeOf(world, slot, CharacterTask.Treat, CharacterTask.Idle),
        }) == StartResult.Started;
    }

    // ---------- the aftercare's reads (task 8.4b, TownAftercare.cpp:409-442, :483-1033; CAP 12) ----------

    /// <summary>C++ <c>getRace()-&gt;robot</c> (TownAftercare.cpp:332-334, :667): <c>RaceData.IsRobot</c> (RaceData.cs:166). False for a stale id or
    /// no race.</summary>
    internal static bool IsRobot(World world, CharacterId id) => Cold(world, id)?.Race?.IsRobot == true;

    /// <summary>C++ <c>isCrippled()</c> (TownAftercare.cpp:674): <c>MedicalState.Crawling</c> (MedicalState.cs:150, Meitou's crippled flag, as
    /// <see cref="MedicallyAble"/>). False for a stale id or no medical state.</summary>
    internal static bool IsCrippled(World world, CharacterId id) => Cold(world, id)?.Medical?.Crawling == true;

    /// <summary>C++ <c>medical-&gt;anatomy</c> as TownAftercare reads it (NeedsRecovery :414-418, TraceMedic :561-566, the stall limb lines
    /// :968-974): per part <c>isRobotic()</c> = <c>HealthPart.IsRobotic(race)</c> (HealthPart.cs:179: a robot race or a replaced limb),
    /// <c>isDead()</c> = a severed limb (<c>Limb == LimbState.Stump</c>), <c>flesh</c>, <c>fleshStun</c>, <c>bandaging</c> = <c>Bandage</c>
    /// (HealthPart.cs:63), <c>maxHealth()</c> = <c>MaxHp</c>, written into <paramref name="into"/> (cleared first; the caller's reusable list: no
    /// allocation per call). Empty for a stale id or no medical state or race.</summary>
    internal static void MedicalParts(World world, CharacterId id, List<AftercarePart> into)
    {
        into.Clear();
        if (Cold(world, id) is not { Medical: { } med, Race: { } race }) return;
        var parts = med.Parts;
        for (int i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            into.Add(new AftercarePart(p.IsRobotic(race), p.Limb == LimbState.Stump, p.Flesh, p.Stun, p.Bandage, p.MaxHp));
        }
    }

    /// <summary>C++ <c>HasAidKit(c, robot)</c> (TownAftercare.cpp:439-442): <c>getBestItemWithFunction(ITEM_FIRSTAID | ITEM_ROBOTREPAIR)</c> with
    /// <c>chargesLeft &gt; 0</c>: an item of the character's main grid or worn backpack whose <c>ItemFunction</c> is
    /// <c>ItemInfo.FirstAidKitFunction</c> (1) or <c>RobotKitFunction</c> (12) (ItemInfo.cs:105, :114) with charges above 0, the places Meitou's
    /// own kit search reads (FirstAidSystem.OwnKit, FirstAidSystem.cs:713-718). False for a stale id or no inventory.</summary>
    internal static bool HasAidKit(World world, CharacterId id, bool robot)
    {
        if (Cold(world, id)?.Inventory is not { } inv) return false;
        int function = robot ? ItemInfo.RobotKitFunction : ItemInfo.FirstAidKitFunction;
        return Charged(inv.Items, function) || Charged(inv.Backpack?.Contents?.Items, function);

        static bool Charged(List<ItemInstance>? items, int function)   // a List and a for loop: no boxed enumerator (polled per medic per second)
        {
            if (items is null) return false;
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.ItemFunction == function && item.Charges > 0 && item.Quantity > 0) return true;
            }
            return false;
        }
    }

    /// <summary>C++ <c>setStandingOrder(M_SET_ORDER_HOLD, hold)</c> then <c>(M_SET_ORDER_PASSIVE, passive)</c> (TownAftercare.cpp:229-230, :306-307):
    /// the Hold and Passive bits of <c>CharacterCold.Orders</c> set directly, as <c>Orders.Apply</c> does (Orders.cs:366-379; see
    /// <see cref="SetBenchStance"/>): Hold on holds where the character stands, off drops the hold point. False for a stale id. Serial step.</summary>
    internal static bool SetStandingOrders(World world, CharacterId id, bool hold, bool passive)
    {
        if (!world.Characters.TryResolveNext(id, out int slot) || world.Characters.Cold(slot) is not { } c) return false;
        bool holding = (c.Orders & CharacterOrders.Hold) != 0;
        if (hold && !holding) { c.Orders |= CharacterOrders.Hold; c.HoldPoint = world.Characters.Next[slot].Position; }
        else if (!hold && holding) { c.Orders &= ~CharacterOrders.Hold; c.HoldPoint = null; }
        if (passive) c.Orders |= CharacterOrders.Passive; else c.Orders &= ~CharacterOrders.Passive;
        return true;
    }

    /// <summary><c>GameConstants.ExtraBloodLossFromBodyparts</c> of the world's BodySystem (BodySystem.cs:57; <c>World.SystemOf</c> is internal,
    /// World.Interface.cs:76, so the public <c>world.Systems</c> list, World.cs:417, is searched). 1 when there is none.</summary>
    static float ExtraBloodLoss(World world)
    {
        var systems = world.Systems;
        for (int i = 0; i < systems.Count; i++)
            if (systems[i] is BodySystem body) return body.Constants.ExtraBloodLossFromBodyparts;
        return 1;
    }

    // ---------- items (CAP 10) ----------

    /// <summary>One inventory item: its record (<c>ItemInstance.Record</c>, ItemInstance.cs:20), name, section (:75, "main" = the grid) and count.</summary>
    internal readonly record struct PgItem(string Record, string Name, string Section, int Quantity);

    /// <summary>C++ <c>Inventory::getAllItems()</c>: every item of <c>CharacterCold.Inventory.Items</c> (Inventory.cs:36), in list order. Empty
    /// for a stale id.</summary>
    internal static PgItem[] Items(World world, CharacterId id) => ItemsWhere(world, id, equippedOnly: false);

    /// <summary>C++ equipped items (<c>getEquippedArmour</c>/<c>getEquippedWeapons</c>): the items in an equipment section
    /// (<c>InventoryCommands.IsEquipmentSection</c>: back, hip, shirt, head, legs, boots, armour, belt, backpack_attach; InventoryCommands.cs:27-28).
    /// Prosthetic limb sections are not included (they belong to the medical state, CAP 13). Empty for a stale id.</summary>
    internal static PgItem[] Equipped(World world, CharacterId id) => ItemsWhere(world, id, equippedOnly: true);

    static PgItem[] ItemsWhere(World world, CharacterId id, bool equippedOnly)
    {
        if (Cold(world, id)?.Inventory is not { } inv) return [];
        var list = new List<PgItem>(inv.Items.Count);
        foreach (var item in inv.Items)
            if (!equippedOnly || InventoryCommands.IsEquipmentSection(item.Section))
                list.Add(new PgItem(item.Record, item.Name, item.Section, item.Quantity));
        return [.. list];
    }

    // ---------- world queries (CAP 6) ----------

    /// <summary>C++ <c>ou-&gt;player-&gt;getAllPlayerCharacters()</c>: every slot used this tick (<c>Next[slot].Alive</c>) whose character is
    /// the player's (<c>IsPlayer</c>), dead ones included, in slot order; ids from <c>Next[slot].Generation</c> (CharacterTable.cs:1373 HighWater,
    /// :1393 Next; not IdOf, :1403, which reads the last tick).</summary>
    internal static List<CharacterId> PlayerCharacters(World world)
    {
        var list = new List<CharacterId>();
        FillPlayerCharacters(world, list);
        return list;
    }

    /// <summary><see cref="PlayerCharacters"/> into <paramref name="list"/> (cleared first; task 9.2b: a caller that asks every tick reuses one
    /// list instead of allocating one per call). The same characters in the same order. Serial step.</summary>
    internal static void FillPlayerCharacters(World world, List<CharacterId> list)
    {
        list.Clear();
        var table = world.Characters;
        var next = table.Next;
        for (int slot = 0; slot < next.Length; slot++)
            if (next[slot].Alive && table.Cold(slot) is { IsPlayer: true }) list.Add(new CharacterId(slot, next[slot].Generation));
    }

    /// <summary>One active player squad: Meitou <c>Squad.Id</c>, name and a copy of its members (src/Meitou.Simulation/Factions/Squads.cs:39, 48, 120).</summary>
    internal sealed record PgSquad(int Id, string Name, IReadOnlyList<CharacterId> Members);

    /// <summary>C++ <c>Faction::getAllActiveSquads()</c> of the player and <c>getActivePlatoon()-&gt;getThings()</c>: <c>world.Squads.All</c>
    /// (World.cs:315, Squads.cs:229) whose <c>Faction</c> is the player's (<c>world.Player.Faction</c>, PlayerState.cs:22) and not <c>Dead</c>,
    /// in registry order. Empty with no player faction.</summary>
    internal static List<PgSquad> PlayerSquads(World world)
    {
        var list = new List<PgSquad>();
        int player = world.Player.Faction;
        if (player < 0) return list;
        foreach (var squad in world.Squads.All)
            if (squad.Faction == player && !squad.Dead) list.Add(new PgSquad(squad.Id, squad.Name, [.. squad.Members]));
        return list;
    }

    /// <summary>Port only (task 4.2's PG-INTERACT line): the character's running task (<c>CharacterHot.Task</c>) as text; "none" for a stale id.</summary>
    internal static string TaskText(World world, CharacterId id) =>
        world.Characters.TryResolveNext(id, out int slot) ? ((CharacterTask)world.Characters.Next[slot].Task).ToString() : "none";

    /// <summary>Task 4.2: <c>PlayerInterface::selectedCharacters</c> (SquadUtil.cpp:84-86): the player's selection in selection order
    /// (<c>world.Player.Selection</c>, PlayerState.cs:91; selection is world state, changed by SelectCommand, PlayerState.cs:12), copied.</summary>
    internal static List<CharacterId> SelectedCharacters(World world) => [.. world.Player.Selection];

    /// <summary>Task 4.2: C++ <c>Building::getPositionMarker(from)</c> (ArenaIngress.cpp:490) of a building that is no bed or cage: the building's
    /// position marker from its layout (<c>WorldObjectLayout.PositionMarker(db, record, position, rotation)</c>, WorldObjectLayout.cs:434, the
    /// branch of <c>getPositionMarker</c> for fewer than two use nodes, FUN_14054b850; Meitou builds a fixture's Marker the same way,
    /// FixtureRegistry.cs:763). A player site's own rotation (BuildSite.Rotation), else a turn of <paramref name="yaw"/> about +Y (TownBuildings.YawOf
    /// inverted). Null when the layout has no marker part, the record is unknown or the layout fails.</summary>
    internal static Vector3? PositionMarker(World world, Meitou.Data.GameDatabase? db, BuildingSource source, string key, string record, Vector3 position, float yaw)
    {
        if (db?.Find(record) is not { } r) return null;
        Quaternion rotation = source == BuildingSource.Player && world.Buildings.Find(key) is { } site
            ? site.Rotation : Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        try { return Meitou.Data.World.WorldObjectLayout.PositionMarker(db, r, position, rotation); }
        catch (Exception) { return null; }   // a layout the data cannot build: the C++ answers the origin, the fallback node follows
    }

    /// <summary>C++ <c>getObjectsWithinSphere(origin, r, CHARACTER, max)</c>: every slot used this tick (<c>Next[slot].Alive</c>) whose position
    /// is within <paramref name="radius"/> (3D, the C++'s sphere), slot order, at most <paramref name="max"/>; ids from <c>Next[slot].Generation</c>.</summary>
    internal static List<CharacterId> CharactersNear(World world, Vector3 centre, float radius, int max = int.MaxValue)
    {
        var next = world.Characters.Next;
        var list = new List<CharacterId>();
        float r2 = radius * radius;
        for (int slot = 0; slot < next.Length && list.Count < max; slot++)
            if (next[slot].Alive && Vector3.DistanceSquared(next[slot].Position, centre) <= r2)
                list.Add(new CharacterId(slot, next[slot].Generation));
        return list;
    }

    /// <summary>Task 8.6: <see cref="CharactersNear(World, Vector3, float, int)"/> into <paramref name="into"/> (cleared first), for a poll that must
    /// not allocate.</summary>
    internal static void CharactersNear(World world, Vector3 centre, float radius, int max, List<CharacterId> into)
    {
        into.Clear();
        var next = world.Characters.Next;
        float r2 = radius * radius;
        for (int slot = 0; slot < next.Length && into.Count < max; slot++)
            if (next[slot].Alive && Vector3.DistanceSquared(next[slot].Position, centre) <= r2)
                into.Add(new CharacterId(slot, next[slot].Generation));
    }

    /// <summary>Where a <see cref="PgBuilding"/> came from.</summary>
    internal enum BuildingSource
    {
        /// <summary>A placed town's building, <c>TownSite.Buildings</c> (PopulationData.cs:203).</summary>
        Town,
        /// <summary>The player's construction, <c>world.Buildings.All</c> (World.cs:372, BuildingRegistry.cs:310).</summary>
        Player,
    }

    /// <summary>A building for the original's building queries. <see cref="Key"/>: a town building's <c>TownBuilding.Key</c> (TownBuildings.cs:160,
    /// the placement key; a town house the player owns and repairs has a construction site with this same id) or a player site's
    /// <c>BuildSite.Id</c>. <see cref="Town"/>: the C++'s <c>Building::getTown()</c> (a town building: its site, which Meitou chose with
    /// TownBuildingBuilder.MemberOf; a player building: its player town, <c>BuildSite.Town</c>, 0 = none, BuildingRegistry.cs:78-81).
    /// <see cref="Complete"/>: <c>getBuildState()-&gt;isComplete</c> (a level building: true unless it is under repair, then its site's
    /// <c>Complete</c>; a player site: <c>BuildSite.Complete</c>).</summary>
    internal sealed record PgBuilding(string Key, string RecordId, string Name, Vector3 Position, float Yaw, CampaignTown Town, bool Complete,
        BuildingSource Source);

    // ---- ONE rule for turning a Meitou building into a PgBuilding, shared by BuildingsNear and FindBuilding (task 4.1 review I3) ----

    /// <summary>Whether a town building is listed at all: not a ruin (<c>world.Fixtures.IsDestroyed(TownBuilding)</c>, FixtureRegistry.Ruins.cs:50).
    /// Unknown in the C++ (its queries filter only <c>isValid()</c>; a destroyed Kenshi building may stay valid), port choice: the original may
    /// pick a destroyed building, the port never does.</summary>
    internal static bool IsListed(World world, Meitou.Data.World.TownBuilding b) => !world.Fixtures.IsDestroyed(b);

    /// <summary>Whether a player site is listed as a player building: not a town house under repair (<c>LevelPlacement</c> set: listed as its
    /// town building, FixtureRegistry.Property.cs:303-347 <c>RegisterTownConstruction</c>) and not destroyed (<c>BuildSite.Destroyed</c>,
    /// BuildingRegistry.cs:123; the same port choice as above).</summary>
    internal static bool IsListed(Meitou.Simulation.Construction.BuildSite s) => s.LevelPlacement is null && !s.Destroyed;

    /// <summary>A listed town building of <paramref name="site"/>: its town, and its completion from its repair site when the player repairs it.</summary>
    internal static PgBuilding FromTown(World world, TownSite site, Meitou.Data.World.TownBuilding b)
    {
        bool complete = world.Buildings.Find(b.Key) is { LevelPlacement: not null } repair ? repair.Complete && !repair.Destroyed : true;
        return new PgBuilding(b.Key, b.BuildingId, b.Name, b.Position, b.Yaw, CampaignTown.OfSite(site.Index), complete, BuildingSource.Town);
    }

    /// <summary>A listed player site: its player town (0 = none), yaw from its rotation (TownBuildings.YawOf, TownBuildings.cs:534).</summary>
    internal static PgBuilding FromPlayer(Meitou.Simulation.Construction.BuildSite s)
    {
        var town = s.Town > 0 ? CampaignTown.OfPlayer(s.Town) : CampaignTown.None;
        return new PgBuilding(s.Id, s.Record, s.Name, s.Position, Meitou.Data.World.TownBuildings.YawOf(s.Rotation), town, s.Complete, BuildingSource.Player);
    }

    /// <summary>
    /// C++ <c>ou-&gt;getObjectsWithinSphere(origin, r, BUILDING, max)</c> (TownArena.cpp:1305 and the like), filtered to
    /// <paramref name="recordIds"/> (null = any record): town buildings of every placed site, then the player's construction, each within
    /// <paramref name="radius"/> of <paramref name="centre"/> in 3D, in that order, at most <paramref name="max"/>. Each building once (a town
    /// house under repair is listed as its town building); ruins and destroyed sites are left out (<see cref="IsListed(World,
    /// Meitou.Data.World.TownBuilding)"/>: unknown in the C++, port choice). Level placements outside every town are not included (header).
    /// </summary>
    internal static List<PgBuilding> BuildingsNear(World world, PopulationData data, Vector3 centre, float radius,
        IReadOnlySet<string>? recordIds = null, int max = int.MaxValue)
    {
        var list = new List<PgBuilding>();
        if (max <= 0) return list;
        float r2 = radius * radius;
        foreach (var site in data.Sites)
        {
            var buildings = site.Buildings;   // copy-on-write list (PopulationData.cs:197-205): take it once
            for (int i = 0; i < buildings.Count; i++)
            {
                var b = buildings[i];
                if (recordIds is not null && !recordIds.Contains(b.BuildingId)) continue;
                if (Vector3.DistanceSquared(b.Position, centre) > r2) continue;
                if (!IsListed(world, b)) continue;
                list.Add(FromTown(world, site, b));
                if (list.Count >= max) return list;
            }
        }
        foreach (var s in world.Buildings.All)
        {
            if (!IsListed(s)) continue;
            if (recordIds is not null && !recordIds.Contains(s.Record)) continue;
            if (Vector3.DistanceSquared(s.Position, centre) > r2) continue;
            list.Add(FromPlayer(s));
            if (list.Count >= max) break;
        }
        return list;
    }

    /// <summary>
    /// Task 4.1: the building with <paramref name="key"/> from <paramref name="source"/> as it is NOW, by the same rule as
    /// <see cref="BuildingsNear"/> (null when it is not listed: gone, a ruin, destroyed). The C++ holds a <c>hand</c> and asks <c>isValid()</c>
    /// each frame (ArenaIngress.cpp:123-125, :215-217); the port resolves the key again. Keys: a player site's <c>BuildSite.Id</c>
    /// (BuildingRegistry.cs:39, saved with the site, Saving/BuildingSave.cs:115-117); a town building's <c>TownBuilding.Key</c> (TownBuildings.cs:160).
    /// Cost: a player key is one dictionary lookup (BuildingRegistry.Find, :314). A town key first tries <paramref name="hint"/> (the site and
    /// list positions where it was found last; a site list is copy-on-write, so a position can move: the key is checked there), then scans
    /// every placed town's list (a few thousand records; no allocation) and updates the hint.
    /// </summary>
    internal static PgBuilding? FindBuilding(World world, PopulationData data, BuildingSource source, string key, ref (int Site, int Index) hint)
    {
        if (source == BuildingSource.Player)
            return world.Buildings.Find(key) is { } s && IsListed(s) ? FromPlayer(s) : null;
        var sites = data.Sites;
        if (hint.Site >= 0 && hint.Site < sites.Count)
        {
            var hs = sites[hint.Site];
            var hb = hs.Buildings;
            if (hint.Index >= 0 && hint.Index < hb.Count && hb[hint.Index].Key == key)
                return IsListed(world, hb[hint.Index]) ? FromTown(world, hs, hb[hint.Index]) : null;
        }
        for (int si = 0; si < sites.Count; si++)
        {
            var site = sites[si];
            var buildings = site.Buildings;
            for (int i = 0; i < buildings.Count; i++)
            {
                if (buildings[i].Key != key) continue;
                hint = (si, i);
                return IsListed(world, buildings[i]) ? FromTown(world, site, buildings[i]) : null;
            }
        }
        hint = (-1, -1);
        return null;
    }

    /// <summary>Task 4.1 review I5: the BUILDING record's functionality record (the C++ use node's functionality GameData, ArenaIdentity.cpp:73-84):
    /// public <c>ProductionSystem.Data(record)</c> (src/Meitou.Simulation/Production/ProductionSystem.cs:181-187, read once per record and kept)
    /// <c>?.Functionality</c> (Meitou.Data/Gameplay/Construction/MachineData.cs:59, set :360 from the record's first <c>functionality</c>
    /// reference). Null when the building has no functionality and no inventory (<c>MachineData.From</c> returns null, :323-327) or no
    /// ProductionSystem runs. Sim thread, serial step only (Data fills a plain dictionary).</summary>
    internal static string? FunctionRecord(Meitou.Simulation.Production.ProductionSystem? production, string record) =>
        production?.Data(record)?.Functionality;

    /// <summary>The world's ProductionSystem (null: none), for <see cref="FunctionRecord"/>: looked up once per world and kept (Meitou's own
    /// per-type cache <c>World.SystemOf&lt;T&gt;</c>, World.Interface.cs:76, is internal), so a per-tick Resolve (task 4.3) walks no system list.</summary>
    internal static Meitou.Simulation.Production.ProductionSystem? Production(World world) =>
        productionOf.GetValue(world, static w => new(w.Systems.OfType<Meitou.Simulation.Production.ProductionSystem>().FirstOrDefault())).Value;

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<World,
        System.Runtime.CompilerServices.StrongBox<Meitou.Simulation.Production.ProductionSystem?>> productionOf = new();

    /// <summary>Task 5.3a: C++ <c>ForgottenGUI::inDialogue()</c> (ContextMenuHooks.cpp:299) on the sim side: the world's DialogueSystem publishes a
    /// conversation view (public <c>PublishConversation()</c>, DialogueSystem.cs:785; set by RefreshView :761-772, cleared by End :695-699 the
    /// moment a line ends the conversation). False without a dialogue system. Sim thread, serial step.</summary>
    internal static bool InDialogue(World world) => Dialogue(world)?.PublishConversation() is not null;

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<World,
        System.Runtime.CompilerServices.StrongBox<Meitou.Simulation.Dialogue.DialogueSystem?>> dialogueOf = new();

    static Meitou.Simulation.Dialogue.DialogueSystem? Dialogue(World world) =>
        dialogueOf.GetValue(world, static w => new(w.Systems.OfType<Meitou.Simulation.Dialogue.DialogueSystem>().FirstOrDefault())).Value;

    /// <summary>Task 7.4: appends <paramref name="observer"/>'s own squad (if any), then every other squad of its faction (public
    /// <c>world.Squads.All</c>, Squads.cs:229; <c>Squad.Id</c> / <c>Faction</c>, :39, :62): the squads whose TEMP_ENEMY on someone that side writes
    /// (the alert in its own squad, a massive alarm in all of its faction's, DialogueSystem.Acts.cs:22-40, 317-338). Nothing for a stale id.
    /// Sim thread, serial step.</summary>
    internal static void FactionSquads(World world, CharacterId observer, List<int> into)
    {
        if (Cold(world, observer) is not { } oc) return;
        if (oc.SquadId >= 0) into.Add(oc.SquadId);
        foreach (var squad in world.Squads.All)
            if (squad.Faction == oc.Faction && squad.Id != oc.SquadId) into.Add(squad.Id);
    }

    /// <summary>Task 7.4: squad <paramref name="squad"/> holds the short-term TEMP_ENEMY tag on <paramref name="target"/> (public
    /// <c>DialogueSystem.Memory</c>, DialogueSystem.cs:281; <c>SquadMemory.HasShort</c>, SquadMemory.cs:168; <c>MemoryTag.TempEnemy</c> = 4,
    /// SquadMemory.cs:21). False without a dialogue system. Sim thread, serial step.</summary>
    internal static bool HoldsTempEnemy(World world, int squad, CharacterId target) =>
        Dialogue(world) is { } d && d.Memory.HasShort(world, squad, target, Meitou.Simulation.Dialogue.MemoryTag.TempEnemy);

    /// <summary>Task 7.4: clears that one tag (public <c>SquadMemory.ClearShort(squad, target, MemoryTag.TempEnemy)</c>, SquadMemory.cs:156-162,
    /// the game's FUN_1406788d0; no other tag is touched). Nothing without a dialogue system. Sim thread, serial step.</summary>
    internal static void ClearTempEnemy(World world, int squad, CharacterId target) =>
        Dialogue(world)?.Memory.ClearShort(squad, target, Meitou.Simulation.Dialogue.MemoryTag.TempEnemy);

    /// <summary>Task 7.4: <c>CharacterCold.RecordId</c> of the character in table slot <paramref name="slot"/> (the patches' key; CharacterTable.cs:133),
    /// null for a slot outside the table or an empty one. Any thread for a read of a settled record id; allocation-free.</summary>
    internal static string? RecordIdAt(World world, int slot) =>
        (uint)slot < (uint)world.Characters.HighWater ? world.Characters.Cold(slot)?.RecordId : null;

    // ---------- speech and messages (CAP 16) ----------

    /// <summary>C++ <c>Character::say(text)</c>: <c>world.Bark(id, text)</c> (World.cs:846-853; a medium bubble, one per speaker, a new one
    /// replaces the old). False (nothing said) for a stale id.</summary>
    internal static bool Bark(World world, CharacterId id, string text)
    {
        if (!Exists(world, id)) return false;
        world.Bark(id, text);
        return true;
    }

    /// <summary>C++ <c>ou-&gt;showPlayerAMessage(text, true)</c>: <c>world.Post(text)</c> (World.cs:838-844; the host shows it as a HUD notice
    /// between ticks, GameHost.FrameShared.cs:122-131).</summary>
    internal static void Message(World world, string text) => world.Post(text);

    /// <summary>Whether the world was loaded from a save (C++: a load rather than <c>CompleteWorldTransition(true)</c> after NEWGAME,
    /// WorldLifecycle.cpp:216-225): <c>World.Saved</c>, "null: a world that started new" (src/Meitou.Simulation/World.cs:321-324), set only by
    /// <c>SaveLoader.Load</c> (src/Meitou.Simulation/Saving/SaveLoader.cs:241, called from GameSaves.cs:456) and never cleared. Task 2.4.</summary>
    internal static bool LoadedFromSave(World world) => world.Saved is not null;

    // ---------- orders and fights (task 3.1, CAP 1, spike S1) ----------

    /// <summary>The orders a fighter had before a spar, as FightStarter remembers them: the toggles (<c>CharacterCold.Orders</c>,
    /// CharacterTable.cs:842), the hold point (<c>HoldPoint</c>, :848) and the speed mode of its moves (<c>RunSpeed</c>, :904; the C++'s
    /// <c>getMovementSpeedOrders()</c>).</summary>
    internal readonly record struct PgOrders(CharacterOrders Orders, Vector3? HoldPoint, byte RunSpeed);

    /// <summary>The toggles a spar stance turns off and the restore puts back: the C++'s PASSIVE (13 = Passive) and HOLD (12 = Hold), plus Block
    /// (11, <c>DEFENSIVE_COMBAT</c>), which stops a fighter attacking in Meitou (CombatSystem.Act.cs:677). The C++'s DEF is
    /// <c>M_SET_ORDER_DEF</c> = Kenshi id 6 ("defensive"); it and AGG (5) set an AI field nothing reads (docs/game/combat.md "Standing orders")
    /// and have no Meitou counterpart, so by the spike S1 decision the port clears and restores Block in DEF's place (divergences.md).</summary>
    internal const CharacterOrders StanceOrders = CharacterOrders.Passive | CharacterOrders.Hold | CharacterOrders.Block;

    /// <summary>C++ <c>getStandingOrder(PASSIVE/HOLD/DEF/AGG)</c> + <c>getMovementSpeedOrders()</c> (FightStarter.cpp:123-127): the
    /// character's orders now. Null for a stale id.</summary>
    internal static PgOrders? Orders(World world, CharacterId id) =>
        Cold(world, id) is { } c ? new PgOrders(c.Orders, c.HoldPoint, c.RunSpeed) : null;

    /// <summary>C++ <c>setStandingOrder(PASSIVE, false)</c>, <c>(HOLD, false)</c>, <c>(DEF, false)</c>, <c>(AGG, true)</c>
    /// (FightStarter.cpp:285-288): Passive, Hold and Block off, set directly as <c>Orders.Apply</c> does (Orders.cs:366-379; spike S1 live);
    /// Hold off drops the hold point as the toggle does (:377). False for a stale id. Serial step.</summary>
    internal static bool SetCombatStance(World world, CharacterId id)
    {
        if (Cold(world, id) is not { } c) return false;
        c.Orders &= ~StanceOrders;
        c.HoldPoint = null;
        return true;
    }

    /// <summary>C++ <c>SetJobsEnabled(c, false)</c> (FightStarter.cpp:70-104): only for a player character (as the C++, :72), the Jobs
    /// toggle (<c>CharacterOrders.Jobs</c>, Orders.cs:56) turned off directly (divergences.md: the original pressed the Jobs key). True when it
    /// was on (the C++'s "changed"). Serial step.</summary>
    internal static bool SetJobsOff(World world, CharacterId id)
    {
        if (Cold(world, id) is not { IsPlayer: true } c || (c.Orders & CharacterOrders.Jobs) == 0) return false;
        c.Orders &= ~CharacterOrders.Jobs;
        return true;
    }

    /// <summary>C++ <c>SetJobsEnabled(c, true)</c> (FightStarter.cpp:414): the Jobs toggle on, set at once (so a save captured before the
    /// next tick holds it, task 2.5) and through <c>OrdersCommand(Jobs, true)</c> for the next tick, whose handler asks the decision step again
    /// (<c>DecisionSystem.Request</c>, Orders.cs:366-374; spike S1). Player characters only (the command's own test, :371). Serial step.</summary>
    internal static bool SetJobsOn(World world, CharacterId id)
    {
        if (Cold(world, id) is not { IsPlayer: true } c) return false;
        c.Orders |= CharacterOrders.Jobs;
        world.Defer(new OrdersCommand([id], CharacterOrders.Jobs, true) { Tick = world.Tick + 1 });
        return true;
    }

    /// <summary>C++ the restore of FightStarter.cpp:394-403: the stance toggles (<see cref="StanceOrders"/>) as saved, the hold point and the
    /// speed mode (<c>setDesiredSpeedOrders</c> + <c>restoreDesiredSpeed</c>). Other toggles (Ranged, Taunt, Sneak, Jobs) are left as they are now.
    /// False for a stale id. Serial step.</summary>
    internal static bool RestoreStance(World world, CharacterId id, PgOrders saved)
    {
        if (Cold(world, id) is not { } c) return false;
        c.Orders = (c.Orders & ~StanceOrders) | (saved.Orders & StanceOrders);
        c.HoldPoint = saved.HoldPoint;
        c.RunSpeed = saved.RunSpeed;
        return true;
    }

    /// <summary>
    /// C++ <c>IssueAttackOrder</c>'s order (FightStarter.cpp:304-328): <c>AttackOrder([attacker], target)</c> (CombatTypes.cs:15-45), deferred
    /// to the next tick (<c>World.Defer</c>, World.cs:794-809), handled by <c>CombatSystem.Inputs</c> -> <c>AttackOrderInput</c>
    /// (CombatSystem.cs:340-401). A player character: <c>Picked = true</c>, a row-5 "Attack target" order through <c>PlayerOrders.Give</c>,
    /// which clears its queue unless <paramref name="queued"/> (the C++'s <c>addOrder(..., shift false, clearExistingOrders, ...)</c>;
    /// OrderQueue.cs:224-241). Anyone else: <c>Picked = false</c>, <c>Source = TaskSource.Npc</c>: the fight starts at once and the AI does not
    /// own it (CAP 1). Never <c>Unprovoked</c> (it books an Assault, CombatSystem.cs:460-469). Serial step.
    /// </summary>
    internal static void Attack(World world, CharacterId attacker, CharacterId target, bool player, bool queued)
    {
        long tick = world.Tick + 1;
        world.Defer(player
            ? new AttackOrder([attacker], target) { Picked = true, Queued = queued, Tick = tick }
            : new AttackOrder([attacker], target) { Picked = false, Source = TaskSource.Npc, Tick = tick });
    }

    /// <summary>The world's <c>CombatSystem</c> from the public <c>world.Systems</c> list (World.cs:417), or null. A linear search: a caller
    /// that asks often keeps the result per world (FightStarter's PgFightWorld does, and logs once when it is missing). No state here.</summary>
    internal static CombatSystem? FindCombat(World world)
    {
        var systems = world.Systems;
        for (int i = 0; i < systems.Count; i++)
            if (systems[i] is CombatSystem combat) return combat;
        return null;
    }

    /// <summary>C++ <c>getAttackTarget().getCharacter()</c> (FightStarter.cpp:602): <c>CombatSystem.TargetOf(slot, generation)</c>
    /// (CombatSystem.cs:242-245, as of the last tick). <see cref="CharacterId.None"/> for nobody or a stale id.</summary>
    internal static CharacterId AttackTarget(World world, CharacterId id) => AttackTarget(world, FindCombat(world), id);

    /// <summary><see cref="AttackTarget(World, CharacterId)"/> with the world's combat system already found (null: reads as nobody).</summary>
    internal static CharacterId AttackTarget(World world, CombatSystem? combat, CharacterId id)
    {
        if (combat is null || !Exists(world, id)) return CharacterId.None;
        var (slot, generation) = combat.TargetOf(id.Slot, id.Generation);
        return slot < 0 ? CharacterId.None : new CharacterId(slot, generation);
    }

    /// <summary>C++ <c>isInCombatMode(true, false)</c> (FightStarter.cpp:620): the melee fight's flag, as the internal
    /// <c>CombatSystem.MeleeCombatMode</c> reads it (CombatSystem.Hud.cs:68-95): <c>CharacterCold.InCombat</c> (CharacterTable.cs:546) and not a
    /// shooting fight (<c>CombatSlot.Ranged</c>, CombatSlot.cs:194, through the public <c>StateOf</c>, CombatSystem.cs:250). False for a stale id.</summary>
    internal static bool InMeleeCombatMode(World world, CharacterId id) => InMeleeCombatMode(world, FindCombat(world), id);

    /// <summary><see cref="InMeleeCombatMode(World, CharacterId)"/> with the world's combat system already found (null: reads as false).</summary>
    internal static bool InMeleeCombatMode(World world, CombatSystem? combat, CharacterId id) =>
        combat is not null && Cold(world, id) is { InCombat: true } && !combat.StateOf(id).Ranged;

    /// <summary>C++ the cleanup of FightStarter.cpp:370-386 (<c>removeJob(FOCUSED_MELEE_ATTACK)</c>, <c>removeJob(UNPROVOKED_...)</c>, the
    /// order queue overwritten, <c>endCombatMode()</c>, <c>clearAllAIGoals()</c>, <c>reThinkCurrentAIAction()</c>): <c>TaskStart.End(world, slot,
    /// TaskEnd.Stopped)</c> (TaskStart.cs:485-505, public, serial phases only): the order queue's front cleared (<c>PlayerOrders.Ending</c>,
    /// OrderQueue.cs:373-381), the fight ended through the combat handler (CombatSystem.cs:589-593), the walk stopped, a new decision asked (CAP 1).
    /// Meitou ends a knocked-out character's task the same way (BodySystem.cs:710-721). False for a stale id. Serial step.</summary>
    internal static bool EndTask(World world, CharacterId id)
    {
        if (!world.Characters.TryResolveNext(id, out int slot)) return false;
        TaskStart.End(world, slot, TaskEnd.Stopped);
        return true;
    }

    // ---------- prisoner release and protection (task 6.2, PrisonerUtil.cpp; CAP 1, 5) ----------

    /// <summary>C++ <c>getAllAttackers(attackers)</c> (PrisonerUtil.cpp:1279): every live character whose fight target is <paramref name="id"/>
    /// as of the last tick (<c>CombatSystem.TargetOf(slot, generation)</c>, CombatSystem.cs:242-245; the same read the engine's internal
    /// <c>AttackerIndex.AttackersOf</c> lists, AttackerIndex.cs:100-103), slot order, appended. Nothing without a combat system or for a stale id.</summary>
    internal static void AttackersOf(World world, CharacterId id, List<CharacterId> into)
    {
        if (FindCombat(world) is not { } combat || !Exists(world, id)) return;
        var next = world.Characters.Next;
        for (int slot = 0; slot < next.Length; slot++)
        {
            if (!next[slot].Alive || slot == id.Slot) continue;
            var (ts, tg) = combat.TargetOf(slot, next[slot].Generation);
            if (ts == id.Slot && tg == id.Generation) into.Add(new CharacterId(slot, next[slot].Generation));
        }
    }

    /// <summary>C++ <c>removeJob(FOCUSED_MELEE_ATTACK)</c> + <c>removeJob(UNPROVOKED_FOCUSED_MELEE_ATTACK)</c> (PrisonerUtil.cpp:795-796,
    /// :1294-1295): a character whose task is a fight (<c>CharacterHot.Task</c> Attack, the task an AttackOrder starts, Movement.cs:30-34) has it
    /// ended through <c>TaskStart.End(world, slot, TaskEnd.Stopped)</c> (TaskStart.cs:485-505); any other task is left alone, as removeJob of a job
    /// the character does not have does nothing. False when nothing was ended or the id is stale. Safe on a dead or downed character (no IsDead guard, as the C++ has none): Meitou itself runs OrderQueue.Clear + TaskStart.End on one
    /// that goes down (BodySystem.Collapse, BodySystem.cs:711-719). Serial step.</summary>
    internal static bool EndAttack(World world, CharacterId id)
    {
        if (!world.Characters.TryResolveNext(id, out int slot)) return false;
        if (world.Characters.Next[slot].Task != (byte)CharacterTask.Attack) return false;
        TaskStart.End(world, slot, TaskEnd.Stopped);
        return true;
    }

    /// <summary>C++ <c>endCombatMode()</c> (PrisonerUtil.cpp:799, :1296): Meitou's own counterpart, internal <c>CombatSystem.Disengage(table, id)</c>
    /// (CombatSystem.cs:595-602, "the end of the fight task (Character::endCombatMode), from any serial phase"; Members.CombatDisengage), for a
    /// character in combat or still with a target, as the engine's own LeaveFight asks (:640-643): its target and swing dropped, the combat flags
    /// cleared, an Attack task stepped to Idle; a walk or any other task is left alone (TaskStart.End would end it). Right after
    /// <see cref="EndAttack"/> ended a fight task there is nothing left to disengage (the task end disengaged it, CombatSystem.cs:589-593), so a pass
    /// that breaks off one character never ends its task twice. Without the member nothing is done (one log line). False when nothing was done.
    /// Serial step.</summary>
    internal static bool EndCombat(World world, CharacterId id)
    {
        if (Cold(world, id) is not { } c || FindCombat(world) is not { } combat) return false;
        bool fighting = c.InCombat || combat.TargetOf(id.Slot, id.Generation).Slot >= 0;
        if (!fighting) return false;
        if (disengage is null)
        {
            if (Members.CombatDisengage is { } m)
            {
                try { disengage = m.CreateDelegate<Action<CombatSystem, CharacterTable, CharacterId>>(); }
                catch (Exception e) { DisengageMissing($"{e.GetType().Name}: {e.Message}"); return false; }
            }
            else { DisengageMissing("not found"); return false; }
        }
        disengage(combat, world.Characters, id);
        return true;
    }

    static Action<CombatSystem, CharacterTable, CharacterId>? disengage;
    static bool disengageMissingLogged;

    static void DisengageMissing(string why)
    {
        if (disengageMissingLogged) return;
        disengageMissingLogged = true;
        ProvingGroundsMod.Ctx?.Log($"prisoner protection: CombatSystem.Disengage {why}; endCombatMode is not done (the fight task is still ended)");
    }

    /// <summary>C++ <c>movement-&gt;setDesiredSpeedOrders(RUN)</c> + <c>setDesiredSpeed(RUN)</c> (PrisonerUtil.cpp:464-469): the move speed mode
    /// <c>CharacterCold.RunSpeed</c> (CharacterTable.cs:904; what a move order walks at, MovementSystem.cs:132) = <c>SpeedMode.Free</c> (2,
    /// Movement.cs:183, no cap): Kenshi's RUN is speed mode 2 (docs/game/dialogue.md:524 "speed mode set to 2 (RUN"; the mode setter gives 999 for
    /// 2, docs/game/pathfinding.md:61), Meitou's mode 1 is the 55-unit jog. A move order given this tick walks at it (it starts next tick).
    /// FightStarter's restore puts a remembered character's mode back (RestoreStance). False for a stale id. Serial step.</summary>
    internal static bool SetRunSpeed(World world, CharacterId id)
    {
        if (Cold(world, id) is not { } c) return false;
        c.RunSpeed = SpeedMode.Free;
        return true;
    }

    /// <summary>C++ <c>getMovementSpeedOrders()</c> (TownAftercare.cpp:182, the medic's remembered speed): the move speed mode
    /// <c>CharacterCold.RunSpeed</c> (CharacterTable.cs:904). <c>SpeedMode.Free</c> for a stale id.</summary>
    internal static byte RunSpeedOf(World world, CharacterId id) => Cold(world, id)?.RunSpeed ?? SpeedMode.Free;

    /// <summary>C++ <c>setDesiredSpeedOrders(speed)</c> + <c>restoreDesiredSpeed()</c> (TownAftercare.cpp:205, the medic's speed given back):
    /// <c>CharacterCold.RunSpeed = speed</c>. False for a stale id. Serial step.</summary>
    internal static bool SetRunSpeedMode(World world, CharacterId id, byte speed)
    {
        if (Cold(world, id) is not { } c) return false;
        c.RunSpeed = speed;
        return true;
    }

    /// <summary>C++ <c>currentGoal(receiver) != NULL</c> (TownAftercare.cpp:211, the hand-off line): the AI's blackboard has a goal now
    /// (<c>AiMind.Goal</c>, Ai/Blackboard.cs:744). False for a stale id or no AI.</summary>
    internal static bool HasAiGoal(World world, CharacterId id) => Cold(world, id)?.Ai?.Goal is not null;

    /// <summary>C++ <c>addOrder(NULL, MOVE_CUS_ORDERED, NULL, false, true, pos)</c> + <c>removeJob(MOVE_CUS_ORDERED)</c> (ClearHandlerOrderQueue,
    /// PrisonerUtil.cpp:472-479: a move that replaces the whole queue, then removed: an empty queue): the order queue emptied
    /// (<c>CharacterCold.OrderQueue.Clear()</c>, CharacterTable.cs:293, OrderQueue.cs:170) and the task ended (<c>TaskStart.End</c>, Stopped),
    /// what Meitou's own Stop does for one character (PlayerSystem.cs:325-332) without its follow reset. False for a stale id. Safe on a dead or downed character (no IsDead guard, as the C++ has none): Meitou itself runs OrderQueue.Clear + TaskStart.End on one
    /// that goes down (BodySystem.Collapse, BodySystem.cs:711-719). Serial step.</summary>
    internal static bool ClearOrders(World world, CharacterId id)
    {
        if (!world.Characters.TryResolveNext(id, out int slot) || world.Characters.Cold(slot) is not { } c) return false;
        c.OrderQueue.Clear();
        TaskStart.End(world, slot, TaskEnd.Stopped);
        return true;
    }

    /// <summary><c>StopCommand(characters)</c> (Commands.cs:154) for the next tick: the combat system disengages each (CombatSystem.cs:355-356),
    /// the player system clears a player character's queue and ends its task (PlayerSystem.cs:322-334); the last step of spike S1's restore.
    /// Nothing for an empty list (an empty command means the player's selection). Serial step.
    /// <para>Ordering trap: an <see cref="Attack"/> deferred earlier in the same tick also runs at tick+1, and Inputs runs PlayerSystem before
    /// CombatSystem (StandardSystems.cs:543/549): the stop clears the player's queue first (PlayerSystem.cs:323-333), then CombatSystem queues
    /// the AttackOrder's pick and only disengages the combat slot (CombatSystem.cs:345-357), so the queued order restarts the fight. FightStarter
    /// therefore stops a fighter ordered in the disengage's tick again with <paramref name="ticksAhead"/> = 2 (AttackOrderStamps).</para></summary>
    internal static void Stop(World world, IReadOnlyList<CharacterId> characters, int ticksAhead = 1)
    {
        if (characters.Count == 0) return;
        world.Defer(new StopCommand(characters) { Tick = world.Tick + ticksAhead });
    }

    // ---------- the Teams 1v1 bench (task 3.3, SparSession.cpp:214-257; CAP 5, spike S10) ----------

    /// <summary>C++ <c>TownAftercare::ReserveBenchOrders(receiver)</c> for an NPC (the town AI hook that kept a benched NPC from its own goals):
    /// <c>CharacterCold.Engaged = true</c> (CharacterTable.cs:938, "the AI leaves it alone", AiSystem.Holds :366), <c>Ai.Walking = false</c>
    /// and <c>TaskStart.Stand</c> once (TaskStart.cs:384), as spike S10 verified live. Re-assert with <see cref="ReassertHold"/> each serial
    /// step (others may clear it, CAP 19). False for a stale id. Serial step.</summary>
    internal static bool HoldStill(World world, CharacterId id)
    {
        if (!world.Characters.TryResolveNext(id, out int slot) || world.Characters.Cold(slot) is not { } c) return false;
        if (c.Ai is { } ai) ai.Walking = false;
        TaskStart.Stand(world.Characters, slot);
        c.Engaged = true;   // last: a throw above leaves no Engaged NPC the caller does not know it holds
        return true;
    }

    /// <summary>Spike S10's re-assert: <c>Engaged = true</c> again when someone cleared it. Serial step.</summary>
    internal static void ReassertHold(World world, CharacterId id)
    {
        if (Cold(world, id) is { Engaged: false } c) c.Engaged = true;
    }

    /// <summary>C++ <c>getCarryingObject()</c> as a character (TownAftercare.cpp:195): <c>CharacterCold.Carrying</c> (None when it carries nobody
    /// or the id is stale).</summary>
    internal static CharacterId CarryingOf(World world, CharacterId id) => Cold(world, id)?.Carrying ?? CharacterId.None;

    /// <summary>C++ <c>inSomething == IN_BED</c> (TownAftercare.cpp:251): <c>CharacterCold.InSomething == HeldState.Bed</c>. False for a stale id.</summary>
    internal static bool InBed(World world, CharacterId id) => Cold(world, id)?.InSomething == Meitou.Simulation.Locks.HeldState.Bed;

    /// <summary>C++ <c>getStandingOrder(M_SET_ORDER_HOLD)</c> / <c>(M_SET_ORDER_PASSIVE)</c> (TownAftercare.cpp:252-253): the Hold and Passive bits of
    /// <c>CharacterCold.Orders</c>. False for a stale id.</summary>
    internal static (bool Hold, bool Passive) HoldPassive(World world, CharacterId id) =>
        Cold(world, id) is { } c ? ((c.Orders & CharacterOrders.Hold) != 0, (c.Orders & CharacterOrders.Passive) != 0) : (false, false);

    /// <summary>Whether the character is held now (<c>CharacterCold.Engaged</c>; the test verbs' listing). False for a stale id.</summary>
    internal static bool IsEngaged(World world, CharacterId id) => Cold(world, id)?.Engaged == true;

    /// <summary>C++ <c>TownAftercare::ReleaseBenchOrders(receiver)</c> for an NPC: <c>Engaged = false</c>, <c>Ai.Replan = true</c> (spike S10:
    /// it walks back to its routine). Serial step.</summary>
    internal static void ReleaseHold(World world, CharacterId id)
    {
        if (Cold(world, id) is not { } c) return;
        c.Engaged = false;
        if (c.Ai is { } ai) ai.Replan = true;
    }

    /// <summary>TEST ONLY (pg town heal, task 8.4c): Meitou's dev-console heal (<c>DebugHeal</c>, DebugCommands.cs:37, applied at the next tick:
    /// MedicalState.Heal; limbs not restored) for one character, an NPC too. False for a stale id. Sim thread.</summary>
    internal static bool DebugHeal(World world, CharacterId id)
    {
        if (!world.Characters.TryResolveNext(id, out _)) return false;
        world.Commands.Enqueue(new Meitou.Simulation.DebugHeal([id]) { Tick = world.Tick + 1 });
        return true;
    }

    /// <summary>Task 8.4c: the native AI asked to decide again (<c>Ai.Replan = true</c>, Blackboard.cs:1070) for a character no owner of the hold
    /// registry holds (TownAftercare's ResumeNative after a bed release, TownAftercare.cpp:396-399). <c>Engaged</c> is left alone. Serial step.</summary>
    internal static void Replan(World world, CharacterId id)
    {
        if (Cold(world, id)?.Ai is { } ai) ai.Replan = true;
    }

    /// <summary>C++ <c>setStandingOrder(PASSIVE, true)</c>, <c>(HOLD, hold)</c>, <c>(AGG, false)</c> (SparSession.cpp:235-237): Passive on,
    /// Hold as given, set directly as <c>Orders.Apply</c> does (Orders.cs:366-379): turning Hold on holds where the character stands (:377-378),
    /// off drops the hold point. AGG has no counterpart (FightStarter's stance note). False for a stale id. Serial step.</summary>
    internal static bool SetBenchStance(World world, CharacterId id, bool hold)
    {
        if (!world.Characters.TryResolveNext(id, out int slot) || world.Characters.Cold(slot) is not { } c) return false;
        c.Orders |= CharacterOrders.Passive;
        bool holding = (c.Orders & CharacterOrders.Hold) != 0;
        if (hold && !holding) { c.Orders |= CharacterOrders.Hold; c.HoldPoint = world.Characters.Next[slot].Position; }
        else if (!hold && holding) { c.Orders &= ~CharacterOrders.Hold; c.HoldPoint = null; }
        return true;
    }

    /// <summary>C++ <c>movement-&gt;isCurrentlyMoving()</c> and <c>movement-&gt;destination</c> (SparSession.cpp:241-243): moving = a horizontal
    /// velocity (<c>CharacterHot.Velocity</c>, CharacterTable.cs:47), the destination = <c>CharacterHot.Goal</c> (X/Z, :64), its height taken
    /// from where the character stands (Meitou's goal has none). Null when standing or stale.</summary>
    internal static Vector3? MoveDestination(World world, CharacterId id)
    {
        if (!world.Characters.TryResolveNext(id, out int slot)) return null;
        ref var n = ref world.Characters.Next[slot];
        if (n.Velocity.X == 0f && n.Velocity.Z == 0f) return null;
        return new Vector3(n.Goal.X, n.Position.Y, n.Goal.Y);
    }

    /// <summary>C++ <c>clearAllAIGoals()</c> + <c>addOrder(NULL, MOVE_CUS_ORDERED, NULL, false, true, pos)</c> + <c>setDestination</c> x2
    /// (SparSession.cpp:247-250): one <c>MoveOrder([id], pos)</c> for the next tick (Commands.cs:23; <c>MovementSystem.GiveMove</c>, any
    /// character, the queue cleared, MovementSystem.cs:357-391; CAP 5). Serial step.</summary>
    internal static void MoveTo(World world, CharacterId id, Vector3 position) =>
        world.Defer(new MoveOrder([id], position) { Tick = world.Tick + 1 });

    /// <summary>C++ <c>lookatPosition(pos, true)</c> (SparSession.cpp:270-271): <c>CharacterHot.Yaw = atan2(dx, dz)</c> (CharacterTable.cs:52;
    /// the engine faces a standing character the same way, CAP 5 "Facing a point"). The attack order given right after turns a fighter too.
    /// False for a stale id. Serial step.</summary>
    internal static bool Face(World world, CharacterId id, Vector3 target)
    {
        if (!world.Characters.TryResolveNext(id, out int slot)) return false;
        ref var n = ref world.Characters.Next[slot];
        float dx = target.X - n.Position.X, dz = target.Z - n.Position.Z;
        if (dx == 0f && dz == 0f) return true;
        n.Yaw = MathF.Atan2(dx, dz);
        return true;
    }

    // ---------- the arena staging (task 4.3b, ArenaIngress.cpp; CAP 5) ----------

    /// <summary>C++ <c>IssueMove</c>'s <c>setStandingOrder(HOLD, false)</c> + <c>(PASSIVE, false)</c> (ArenaIngress.cpp:435-437, "Hold/Passive
    /// ignore move tasks; clear so pathing actually starts"): Hold and Passive off, set directly as <c>Orders.Apply</c> does (Orders.cs:366-379);
    /// Hold off drops the hold point (:377). Block is left alone. False for a stale id. Serial step.</summary>
    internal static bool SetMoveStance(World world, CharacterId id)
    {
        if (Cold(world, id) is not { } c) return false;
        c.Orders &= ~(CharacterOrders.Passive | CharacterOrders.Hold);
        c.HoldPoint = null;
        return true;
    }

    /// <summary>C++ <c>removeJob(MOVE_CUS_ORDERED)</c> (ArenaIngress.cpp:603, :795, :801, :1810): a character walking (<c>CharacterHot.Task</c>
    /// GoTo, the task a <c>MoveOrder</c> starts, MovementSystem.cs:357-391) has its walk ended through <c>TaskStart.End(world, slot,
    /// TaskEnd.Stopped)</c> (TaskStart.cs:485-505: the order queue's front cleared, the character stands); any other task is left alone, as
    /// removeJob of a job the character does not have does nothing. False when nothing was ended or the id is stale. Serial step.</summary>
    internal static bool EndWalk(World world, CharacterId id)
    {
        if (!world.Characters.TryResolveNext(id, out int slot)) return false;
        if (world.Characters.Next[slot].Task != (byte)CharacterTask.GoTo) return false;
        TaskStart.End(world, slot, TaskEnd.Stopped);
        return true;
    }

    /// <summary>C++ <c>getMovementSpeed()</c> (ArenaIngress.cpp:900, the log lines :709, :1680): the horizontal speed, world units per second
    /// (<c>CharacterHot.Velocity</c>, CharacterTable.cs:45-47). 0 for a stale id.</summary>
    internal static float MovementSpeed(World world, CharacterId id)
    {
        if (!world.Characters.TryResolveNext(id, out int slot)) return 0f;
        var v = world.Characters.Next[slot].Velocity;
        return MathF.Sqrt(v.X * v.X + v.Z * v.Z);
    }

    // ---------- the ring guard (task 3.4, ArenaRingGuard.cpp:117-139; CAP 5) ----------

    /// <summary>C++ <c>combatMover.hasForcedWP = false; _setPositionSimple(target)</c> (ArenaRingGuard.cpp:123-124), and the port's
    /// near-band step in place of <c>combatMover.setForcedWP(target)</c> (:134). The C++ keeps the fighter's height and leaves the rest to
    /// Kenshi's physics; Meitou has none to resolve a bad spot, so, as its own relocations do (AnimationSystem.Relocate refuses a spot that is
    /// not walkable, AnimationSystem.cs:753-754; DebugCommands.Teleport puts the body on GroundHeight, DebugCommands.cs:608): the spot must be
    /// walkable for a character standing near <paramref name="nearY"/> and its height is the ground there at that floor
    /// (<c>IWalkability.TryStand(x, z, nearY, out y)</c>, Walkability.cs:69-73; <c>world.Walkability</c>, World.cs:299). Then
    /// <c>CharacterTable.Place(slot, position, yaw, task)</c> (CharacterTable.cs:1524-1536, public, serial phases only: throws in Parallel)
    /// with the character's own <c>Yaw</c> and current <c>Task</c> (CharacterTable.cs:52, :89), so a fight goes on (CAP 5: starting a task
    /// would end it, TaskStart.cs:281-283). Place also zeroes the velocity, sets the goal to the new spot and drops any path (:1530-1532); the
    /// combat footwork re-paths from there (Fighting.cs:154). Never call it on a downed or carried body (the ragdoll's, CAP 5). False (nothing
    /// moved) for a stale id or a spot that is not walkable. Serial step.</summary>
    internal static bool TryPlaceGroundedKeepingTask(World world, CharacterId id, float x, float nearY, float z)
    {
        if (!world.Characters.TryResolveNext(id, out int slot)) return false;
        if (!world.Walkability.TryStand(x, z, nearY, out float y)) return false;
        ref var n = ref world.Characters.Next[slot];
        world.Characters.Place(slot, new Vector3(x, y, z), n.Yaw, (CharacterTask)n.Task);
        return true;
    }

    /// <summary>Whether a character standing near height <paramref name="nearY"/> may stand at (x, z): <c>IWalkability.IsWalkable(x, z, nearY)</c>
    /// (Walkability.cs:58; <c>world.Walkability</c>, World.cs:299). For the test-only <c>pg ring push</c>.</summary>
    internal static bool IsWalkable(World world, float x, float nearY, float z) => world.Walkability.IsWalkable(x, z, nearY);

    /// <summary><c>IWalkability.TryStand(x, z, nearY, out y)</c> (Walkability.cs:69-73), the test of <see cref="TryPlaceGroundedKeepingTask"/>
    /// without moving anyone. For the test-only <c>pg ring sample</c> (task 4.8).</summary>
    internal static bool TryStandAt(World world, float x, float nearY, float z, out float y) => world.Walkability.TryStand(x, z, nearY, out y);

    /// <summary>Task 3.5 (test output): <c>CharacterCold.InCombat</c>; false for a stale id.</summary>
    internal static bool InCombat(World world, CharacterId id) => Cold(world, id)?.InCombat == true;

    /// <summary>Task 3.5 (test output): the running task (<c>CharacterCold.Running</c>, TaskStart.cs:166: row, source, target, start tick), or
    /// null for none or a stale id.</summary>
    internal static RunningTask? Running(World world, CharacterId id) => Cold(world, id)?.Running;

    /// <summary>Task 6.1: C++ <c>isChainedMode()</c> (LeaderboardStore.cpp:120, PrisonerUtil.cpp:752): Meitou's chained flag, public
    /// <c>LockSystem.Chained(CharacterCold)</c> (src/Meitou.Simulation/Locks/LockSystem.Shackles.cs:73-77: wears a piece with a lock, locked or
    /// not, as the original's Character+0x320 set by equipping shackles). False for a stale id.</summary>
    internal static bool IsChained(World world, CharacterId id) => Cold(world, id) is { } c && Meitou.Simulation.Locks.LockSystem.Chained(c);

    /// <summary>Carried by someone: <c>CharacterCold.CarriedBy</c> set (CharacterTable.cs:770). A carried body is the ragdoll's
    /// (RagdollSystem.Wanted, RagdollSystem.cs:449-451). False for a stale id.</summary>
    internal static bool IsCarried(World world, CharacterId id) => Cold(world, id) is { } c && !c.CarriedBy.IsNone;

    // ---------- the cage return's carry (task 6.3, PrisonerUtil.cpp:666-698, 768-784; CAP 12, spike S11) ----------

    /// <summary>C++ <c>isCarryingSomething</c>: <c>CharacterCold.Carrying</c> set (CharacterTable.cs). False for a stale id.</summary>
    internal static bool IsCarrying(World world, CharacterId id) => Cold(world, id) is { } c && !c.Carrying.IsNone;

    /// <summary>The world's carry system (<c>world.Systems</c>, World.cs:417; <c>SystemOf</c> is internal), or null.</summary>
    internal static CarrySystem? FindCarry(World world)
    {
        var systems = world.Systems;
        for (int i = 0; i < systems.Count; i++)
            if (systems[i] is CarrySystem carry) return carry;
        return null;
    }

    /// <summary>C++ <c>handler-&gt;addOrder(NULL, LIFT_PERSON_PLAYER_ORDER, prisoner, false, true, pos)</c> (IssueLiftOrder, PrisonerUtil.cpp:775-781,
    /// "Let LIFT_PERSON_PLAYER_ORDER walk to and pick up the prisoner"): public <c>CarrySystem.BeginPickUp(world, carrier, body, Idle, false,
    /// TaskSource.Goal)</c> (CarrySystem.cs:545-555, row LiftPersonPlayerOrder, the player order's row; the pick-up task walks to the body and
    /// lifts it, spike S8's hospital run). The mod's own carrier passes the 3.2b pick-up refusal (CombatMatchView.IsOwnCarrier). False when the
    /// carrier cannot carry, the body cannot be lifted (awake), an id is stale or there is no carry system. Serial step.</summary>
    internal static bool BeginLift(World world, CharacterId carrier, CharacterId body)
    {
        if (!world.Characters.TryResolveNext(carrier, out int slot) || FindCarry(world) is not { } carry) return false;
        return carry.BeginPickUp(world, slot, body, CharacterTask.Idle, kidnap: false, source: TaskSource.Goal);
    }

    /// <summary>C++ <c>dropCarriedObject(false, false)</c> (FinishCageEntry, PrisonerUtil.cpp:678-679): public <c>CarrySystem.LetGo(world,
    /// carrier)</c> (CarrySystem.cs:604-614: the body let go where the carrier stands, without the put-down knock-out; what the arrest system's
    /// put-in-cage does, and spike S11's "let go first": Imprison refuses a carried body). False when it carries nothing or the id is stale.
    /// Serial step.</summary>
    internal static bool LetGo(World world, CharacterId carrier)
    {
        if (!world.Characters.TryResolveNext(carrier, out int slot) || FindCarry(world) is not { } carry) return false;
        return carry.LetGo(world, slot);
    }

    // ---------- the probe (pg probe, spike S14) ----------

    /// <summary>Every adapter value for one character as one JSON object (sim thread). Floats through <see cref="CMath.FormatG(double, int)"/>
    /// (9 significant digits); a non-finite value (NaN, an infinity) as <c>null</c>. For a stale id only <c>{"id":...,"exists":false}</c>.
    /// <paramref name="extra"/>: more <c>,"name":value</c> fields from other subsystems (the fighter identity, task 2.3), put before the
    /// closing brace of an existing character's object.</summary>
    internal static string ProbeJson(World world, PopulationData data, Towns towns, CharacterId id, string extra = "")
    {
        var j = new StringBuilder(1024);
        bool exists = Exists(world, id);
        j.Append("{\"id\":").Append(Str(CharacterIdText.Format(id))).Append(",\"tick\":").Append(world.Tick.ToString(CultureInfo.InvariantCulture))
            .Append(",\"exists\":").Append(Bool(exists));   // tick: task 3.5 (the scenarios time events by sim ticks)
        if (!exists || Position(world, id) is not { } pos) return j.Append('}').ToString();

        int? faction = Faction(world, id);
        j.Append(",\"name\":").Append(Str(Name(world, id)))
            .Append(",\"record\":").Append(Str(RecordId(world, id)))
            .Append(",\"faction\":").Append(faction is { } f ? f.ToString(CultureInfo.InvariantCulture) : "null")
            .Append(",\"factionName\":").Append(Str(FactionName(world, data, faction)))
            .Append(",\"player\":").Append(Bool(IsPlayer(world, id)))
            .Append(",\"dead\":").Append(Bool(IsDead(world, id)))
            .Append(",\"unconscious\":").Append(Bool(IsUnconscious(world, id)))
            .Append(",\"incapacitated\":").Append(Bool(IsIncapacitated(world, id)))
            .Append(",\"position\":[").Append(Num(pos.X)).Append(',').Append(Num(pos.Y)).Append(',').Append(Num(pos.Z)).Append(']')
            .Append(",\"yaw\":").Append(Num(Yaw(world, id)));

        // task 3.1: the orders FightStarter remembers and restores, and the fight state
        if (Orders(world, id) is { } o)
            j.Append(",\"orders\":").Append(Str(o.Orders.ToString()))
                .Append(",\"holdPoint\":").Append(o.HoldPoint is { } hp ? "[" + Num(hp.X) + "," + Num(hp.Y) + "," + Num(hp.Z) + "]" : "null")
                .Append(",\"runSpeed\":").Append(o.RunSpeed.ToString(CultureInfo.InvariantCulture));
        var target = AttackTarget(world, id);
        j.Append(",\"inCombat\":").Append(Bool(Cold(world, id)?.InCombat == true))
            .Append(",\"inMeleeCombatMode\":").Append(Bool(InMeleeCombatMode(world, id)))
            .Append(",\"attackTarget\":").Append(target.IsNone ? "null" : Str(CharacterIdText.Format(target)))
            .Append(",\"task\":").Append(Str(world.Characters.TryResolveNext(id, out int ts) ? ((CharacterTask)world.Characters.Next[ts].Task).ToString() : null))
            // task 8.4b: the building whose indoor volume holds the character (SightMap.BuildingOf, SightMap.cs:605; null outdoors), the scenarios'
            // "inside the hospital" read
            .Append(",\"building\":").Append(Str(world.Characters.TryResolveNext(id, out int bs) ? world.Sight.BuildingOf(bs) : null))
            .Append(",\"orderQueue\":").Append((Cold(world, id)?.OrderQueue.Count ?? 0).ToString(CultureInfo.InvariantCulture));
        // task 4.8 (diagnostic): the walk's movement flags, goal and speed, and what holds the character (a seat, a bed)
        // "goal" is [x, z]: CharacterHot.Goal is a Vector2 whose Y is the world Z (CharacterTable.cs:64, as MoveDestination reads it above)
        if (world.Characters.TryResolveNext(id, out int ms))
        {
            ref var mn = ref world.Characters.Next[ms];
            j.Append(",\"moveFlags\":").Append(Str(((MoveFlags)mn.Flags).ToString()))
                .Append(",\"goal\":[").Append(Num(mn.Goal.X)).Append(',').Append(Num(mn.Goal.Y)).Append(']')
                .Append(",\"speed\":").Append(Num(MovementSpeed(world, id)))
                .Append(",\"inSomething\":").Append(Str(Cold(world, id)?.InSomething.ToString()));
        }
        // task 3.5: who started the running task and when (CharacterCold.Running, TaskStart.cs:166: row, source, target, start tick), so a
        // scenario can tell a fight given after a match ended (a reaction, a goal, a player order) from one left over
        if (Cold(world, id)?.Running is { } run)
            j.Append(",\"running\":{\"row\":").Append(Str(run.Row.ToString())).Append(",\"source\":").Append(Str(run.Source.ToString()))
                .Append(",\"target\":").Append(run.Target.IsNone ? "null" : Str(CharacterIdText.Format(run.Target)))
                .Append(",\"tick\":").Append(run.Tick.ToString(CultureInfo.InvariantCulture)).Append('}');
        else
            j.Append(",\"running\":null");
        // task 3.5: the crime and relation state a sparring match must leave untouched (CAP 1 "No crime, no relation change"): the character's
        // bounties summed over factions and its unbooked crime (CharacterCold.Bounties / Pending, CharacterTable.cs:922-927), the faction
        // relations' change counter (FactionRelations.Changes, FactionRelations.cs:72: any relation event anywhere moves it) and the town's
        // relation to the player both ways (as pg census, Census.cs:103-105)
        int bounty = 0;
        if (Cold(world, id)?.Bounties is { } bounties) foreach (var e in bounties.Entries) bounty += e.Amount;
        j.Append(",\"bounty\":").Append(bounty.ToString(CultureInfo.InvariantCulture))
            .Append(",\"pendingCrime\":").Append(Bool(Cold(world, id)?.Pending is not null))
            .Append(",\"engaged\":").Append(Bool(Cold(world, id)?.Engaged == true));   // the bench hold (spike S10): task 3.5 checks none is left

        var s = Stats(world, id);
        j.Append(",\"stats\":{\"valid\":").Append(Bool(s.Valid))
            .Append(",\"meleeAttack\":").Append(Num(s.MeleeAttack)).Append(",\"meleeDefence\":").Append(Num(s.MeleeDefence))
            .Append(",\"dodge\":").Append(Num(s.Dodge)).Append(",\"strength\":").Append(Num(s.Strength))
            .Append(",\"toughness\":").Append(Num(s.Toughness)).Append(",\"dexterity\":").Append(Num(s.Dexterity))
            .Append(",\"katanas\":").Append(Num(s.Katanas)).Append(",\"sabres\":").Append(Num(s.Sabres))
            .Append(",\"blunt\":").Append(Num(s.Blunt)).Append(",\"heavyWeapons\":").Append(Num(s.HeavyWeapons))
            .Append(",\"hackers\":").Append(Num(s.Hackers)).Append(",\"martialArts\":").Append(Num(s.MartialArts))
            .Append(",\"crossbows\":").Append(Num(s.Crossbows)).Append(",\"turrets\":").Append(Num(s.Turrets))
            .Append(",\"polearms\":").Append(Num(s.Polearms)).Append(",\"scale\":").Append(Num(s.Scale))
            .Append(",\"scaledToughness\":").Append(Num(s.ScaledToughness)).Append('}')
            .Append(",\"overallSkill\":").Append(Num(Core.Glue.OverallSkill.Of(s)))
            .Append(",\"bloodRatio\":").Append(Num(BloodRatio(world, id)))
            .Append(",\"aidNeed\":").Append(Num(AidNeed(world, id, false)))
            .Append(",\"aidNeedRobot\":").Append(Num(AidNeed(world, id, true)))
            .Append(",\"wounds\":").Append(WoundCount(world, id).ToString(CultureInfo.InvariantCulture))   // task 4.7 (test output)
            .Append(",\"bleedRate\":").Append(Num(BleedRate(world, id)))
            .Append(",\"bandage\":").Append(Num(BandageTotal(world, id)));

        j.Append(",\"limbs\":[");
        var parts = LimbHealth(world, id);
        for (int i = 0; i < parts.Length; i++)
            j.Append(i > 0 ? "," : "").Append("{\"name\":").Append(Str(parts[i].Name)).Append(",\"flesh\":").Append(Num(parts[i].Flesh))
                .Append(",\"stun\":").Append(Num(parts[i].Stun)).Append(",\"maxHp\":").Append(Num(parts[i].MaxHp)).Append('}');
        j.Append(']');

        j.Append(",\"equipped\":[");
        var eq = Equipped(world, id);
        for (int i = 0; i < eq.Length; i++)
            j.Append(i > 0 ? "," : "").Append("{\"record\":").Append(Str(eq[i].Record)).Append(",\"name\":").Append(Str(eq[i].Name))
                .Append(",\"section\":").Append(Str(eq[i].Section)).Append(",\"quantity\":").Append(eq[i].Quantity.ToString(CultureInfo.InvariantCulture)).Append('}');
        j.Append("],\"items\":").Append(Items(world, id).Length.ToString(CultureInfo.InvariantCulture));

        var town = towns.CurrentTownOf(world, id);   // the character's town (getCurrentTownLocation, task 7.2 review); pointTown = the radius rule
        j.Append(",\"town\":{\"site\":").Append(town.Site.ToString(CultureInfo.InvariantCulture))
            .Append(",\"playerTown\":").Append(town.PlayerTown.ToString(CultureInfo.InvariantCulture))
            .Append(",\"name\":").Append(Str(towns.NameOf(world, town)))
            .Append(",\"scratch\":").Append(Bool(towns.IsScratch(town))).Append('}')
            .Append(",\"pointTown\":").Append(towns.TownAt(world, pos).Site.ToString(CultureInfo.InvariantCulture))
            .Append(",\"scratchSite\":").Append((towns.Scratch?.Index ?? -1).ToString(CultureInfo.InvariantCulture));
        var rel = data.Relations;
        var site = town.PlayerTown == 0 && town.Site >= 0 ? data.Sites.FirstOrDefault(x => x.Index == town.Site) : null;
        int tf = site?.Town.Faction is { } tfName ? rel.IndexOf(tfName) : -1, pf = data.PlayerFaction;
        bool both = rel.Contains(tf) && rel.Contains(pf);
        j.Append(",\"relations\":{\"changes\":").Append(rel.Changes.ToString(CultureInfo.InvariantCulture))
            .Append(",\"townToPlayer\":").Append(both ? Num(rel.Get(tf, pf)) : "null")
            .Append(",\"playerToTown\":").Append(both ? Num(rel.Get(pf, tf)) : "null").Append('}');
        return j.Append(extra).Append('}').ToString();
    }

    static string Bool(bool b) => b ? "true" : "false";
    static string Str(string? s) => s is null ? "null" : System.Text.Json.JsonSerializer.Serialize(s);
    static string Num(float v) => float.IsFinite(v) ? CMath.FormatG(v, 9) : "null";
}
