﻿using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.Audio;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.ModLoader.Config;
using Terraria.ModLoader.IO;

namespace BossChecklist
{
	public class PlayerAssist : ModPlayer {
		// For the 'never opened' button glow for players who haven't noticed the new feature yet.
		public bool hasOpenedTheBossLog;
		// When players jon a different world, the boss log PageNum should reset back to its original state
		public bool enteredWorldReset;
		public bool PlayerRecordsInitialized;

		// Records are bound to characters, but records are independent between worlds as well.
		// AllStored records contains every player record from every world
		// RecordsForWorld is a reference to the specfic player records of the current world
		// We split up AllStoredRecords with 'Main.ActiveWorldFileData.UniqueId.ToString()' as keys
		public Dictionary<string, List<PersonalRecords>> AllStoredRecords;

		/// <summary>
		/// Fetches the list of records assigned to the current world from the list of all stored records.
		/// Do NOT reference when on the game menu.
		/// </summary>
		public List<PersonalRecords> RecordsForWorld => AllStoredRecords[Main.ActiveWorldFileData.UniqueId.ToString()];
		public List<ItemDefinition> BossItemsCollected;

		public const int RecordState_NoRecord = 0;
		public const int RecordState_PersonalBest = 1;
		public const int RecordState_WorldRecord = 2;
		public int NewRecordState = 0;
		public bool[] hasNewRecord;

		public void SubmitCombatText(int recordIndex) {
			if (NewRecordState == RecordState_PersonalBest)
				CombatText.NewText(Player.getRect(), Color.LightYellow, Language.GetTextValue($"{BossLogUI.LangLog}.Records.NewRecord"), true);
			else if (NewRecordState == RecordState_WorldRecord)
				CombatText.NewText(Player.getRect(), Color.LightYellow, Language.GetTextValue($"{BossLogUI.LangLog}.Records.NewWorldRecord"), true);

			if (NewRecordState != RecordState_NoRecord)
				hasNewRecord[recordIndex] = true;

			NewRecordState = RecordState_NoRecord;
		}

		public override void Initialize() {
			hasOpenedTheBossLog = false;
			enteredWorldReset = false;
			PlayerRecordsInitialized = false;

			AllStoredRecords = new Dictionary<string, List<PersonalRecords>>();
			BossItemsCollected = new List<ItemDefinition>();

			hasNewRecord = Array.Empty<bool>();
		}

		public override void SaveData(TagCompound tag) {
			// We cannot save dictionaries, so we'll convert it to a TagCompound instead
			TagCompound Record_Data = new TagCompound();
			foreach (KeyValuePair<string, List<PersonalRecords>> data in AllStoredRecords) {
				TagCompound Record_PerWorld = new TagCompound(); // new list of records for each world
				foreach (PersonalRecords record in data.Value) {
					if (record.CanBeSaved)
						Record_PerWorld.Add(record.BossKey, record.SerializeData()); // serialize the boss key and records (for each world)
				}
				Record_Data.Add(data.Key, Record_PerWorld);
			}

			tag["Record_Data"] = Record_Data;
			tag["BossLogPrompt"] = hasOpenedTheBossLog;
			tag["BossLootObtained"] = BossItemsCollected;
		}

		public override void LoadData(TagCompound tag) {
			hasOpenedTheBossLog = tag.GetBool("BossLogPrompt"); // saved state of the unopened boss log prompt
			BossItemsCollected = tag.GetList<ItemDefinition>("BossLootObtained").ToList(); // Prepare the collectibles for the player.

			if (tag.TryGet("Record_Data", out TagCompound savedData)) {
				AllStoredRecords.Clear();
				// foreach unique world key
				foreach (KeyValuePair<string, object> data in savedData) {
					List<PersonalRecords> RecordsByWorldKey = new List<PersonalRecords>();
					// foreach 
					foreach (KeyValuePair<string, object> listofrecords in data.Value as TagCompound) {
						RecordsByWorldKey.Add(PersonalRecords.DESERIALIZER(listofrecords.Value as TagCompound)); // deserialize the saved record data
					}
					AllStoredRecords.Add(data.Key, RecordsByWorldKey); // add each world key to all stored records
				}
			}
		}

		public override void OnEnterWorld() {
			// PageNum starts out with an invalid number so jumping between worlds will always reset the BossLog when toggled
			enteredWorldReset = true;

			// Upon entering a world, determine if records already exist for a player and copy them into 'RecordsForWorld'
			// If personal records do not exist for this world, create a new entry for the player to use
			string WorldID = Main.ActiveWorldFileData.UniqueId.ToString();
			if (AllStoredRecords.TryGetValue(WorldID, out List<PersonalRecords> tempRecords)) {
				List<PersonalRecords> unloadedRecords = new List<PersonalRecords>();
				List<PersonalRecords> sortedRecords = new List<PersonalRecords>();
				foreach (PersonalRecords record in tempRecords) {
					if (!BossChecklist.bossTracker.BossRecordKeys.Contains(record.BossKey))
						unloadedRecords.Add(record); // any saved records from an unloaded boss must be perserved
				}

				// iterate through the record keys to keep the data in order
				foreach (string key in BossChecklist.bossTracker.BossRecordKeys) {
					int index = tempRecords.FindIndex(x => x.BossKey == key);
					sortedRecords.Add(index == -1 ? new PersonalRecords(key) : tempRecords[index]);
				}

				AllStoredRecords[WorldID] = sortedRecords.Concat(unloadedRecords).ToList();
			}
			else {
				List<PersonalRecords> NewRecordListForWorld = new List<PersonalRecords>();
				foreach (string key in BossChecklist.bossTracker.BossRecordKeys) {
					NewRecordListForWorld.Add(new PersonalRecords(key));
				}
				AllStoredRecords.Add(WorldID, NewRecordListForWorld); // A new entry will be added to AllStoredRecords so that it can be saved when needed
			}
			PlayerRecordsInitialized = true;

			hasNewRecord = new bool[BossChecklist.bossTracker.BossRecordKeys.Count];

			if (BossChecklist.DebugConfig.DISABLERECORDTRACKINGCODE)
				return;

			// When a player joins a world, their Personal Best records will need to be sent to the server for new Personal Best comparing
			// The server doesn't need player records from every world, just the current one
			if (Main.netMode == NetmodeID.MultiplayerClient) {
				ModPacket packet = Mod.GetPacket();
				packet.Write((byte)PacketMessageType.SendPersonalBestRecordsToServer);
				foreach (string key in BossChecklist.bossTracker.BossRecordKeys) {
					int index = RecordsForWorld.FindIndex(x => x.BossKey == key);
					if (index != -1) {
						packet.Write(RecordsForWorld[index].durationBest);
						packet.Write(RecordsForWorld[index].hitsTakenBest);
					}
				}
				packet.Send(); // Multiplayer client --> Server

				packet = Mod.GetPacket(); // new packet
				packet.Write((byte)PacketMessageType.RequestWorldRecords);
				packet.Send(); // Multiplayer client --> Server
			}
		}

		// Track each tick that passes during boss fights.
		public override void PreUpdate() {
			/* Debug tool for opening the Progression Mode prompt
			if (Main.keyState.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.RightControl))
				hasOpenedTheBossLog = false;
			*/

			if (BossChecklist.DebugConfig.DISABLERECORDTRACKINGCODE || Player.whoAmI == 255)
				return;

			if (Main.netMode == NetmodeID.SinglePlayer && !BossChecklist.ClientConfig.RecordTrackingEnabled)
				return;

			List<PersonalRecords> EntryRecords = Main.netMode == NetmodeID.Server ? BossChecklist.ServerCollectedRecords[Player.whoAmI] : RecordsForWorld;
			foreach (PersonalRecords record in EntryRecords) {
				if (record.IsCurrentlyBeingTracked)
					record.Tracker_Duration++;
			}
		}

		// Track amount of times damage was taken during a boss fight. Source of damage does not matter.
		public override void OnHurt(Player.HurtInfo info) {
			if (BossChecklist.DebugConfig.DISABLERECORDTRACKINGCODE || Player.whoAmI == 255)
				return;

			if (Main.netMode == NetmodeID.SinglePlayer && !BossChecklist.ClientConfig.RecordTrackingEnabled)
				return;

			List<PersonalRecords> EntryRecords = Main.netMode == NetmodeID.Server ? BossChecklist.ServerCollectedRecords[Player.whoAmI] : RecordsForWorld;
			foreach (PersonalRecords record in EntryRecords) {
				if (record.IsCurrentlyBeingTracked)
					record.Tracker_HitsTaken++;
			}
		}

		// Track player deaths during boss fights.
		public override void Kill(double damage, int hitDirection, bool pvp, PlayerDeathReason damageSource) {
			if (BossChecklist.DebugConfig.DISABLERECORDTRACKINGCODE || Player.whoAmI == 255)
				return;

			if (Main.netMode == NetmodeID.SinglePlayer && !BossChecklist.ClientConfig.RecordTrackingEnabled)
				return;

			List<PersonalRecords> EntryRecords = Main.netMode == NetmodeID.Server ? BossChecklist.ServerCollectedRecords[Player.whoAmI] : RecordsForWorld;
			foreach (PersonalRecords record in EntryRecords) {
				if (record.IsCurrentlyBeingTracked)
					record.Tracker_Deaths++;
			}
		}

		// Record tracking should stop if the player disconnects from the world.
		public override void PlayerDisconnect() {
			if (BossChecklist.DebugConfig.DISABLERECORDTRACKINGCODE || Player.whoAmI == 255)
				return;

			if (Main.netMode == NetmodeID.SinglePlayer && !BossChecklist.ClientConfig.RecordTrackingEnabled)
				return;

			if (Main.netMode == NetmodeID.Server) {
				foreach (PersonalRecords record in BossChecklist.ServerCollectedRecords[Player.whoAmI]) {
					record.StopTracking_Server(Player.whoAmI, false, false);
				}
			}
			else {
				foreach (PersonalRecords record in RecordsForWorld) {
					record.StopTracking(false, false); // Note: Disconnecting still tracks attempts and deaths. Does not save last attempt data.
				}
			}
		}
		
		// Respawn timer feature
		public override void UpdateDead() {
			if (Main.netMode == NetmodeID.Server || Player.whoAmI == 255)
				return;

			// Timer sounds when a player is about to respawn
			if (BossChecklist.ClientConfig.TimerSounds && Player.respawnTimer > 0 && Player.respawnTimer <= 180 && Player.respawnTimer % 60 == 0)
				SoundEngine.PlaySound(SoundID.MaxMana);
		}

		// Adds items that are picked up to the collected boss loot list
		public override bool OnPickup(Item item) {
			if (Main.netMode == NetmodeID.Server || Player.whoAmI == 255)
				return base.OnPickup(item);

			// Only add the item to the list if it is not already present
			if (BossChecklist.bossTracker.EntryLootCache[item.type] && !BossItemsCollected.Any(x => x.Type == item.type))
				BossItemsCollected.Add(new ItemDefinition(item.type));

			return base.OnPickup(item);
		}

		/*
		
		// Temporarily(?) removed
		
		public override void OnCreated(Item item, ItemCreationContext context) {
			if (Main.netMode != NetmodeID.Server && BossChecklist.bossTracker.EntryLootCache[item.type]) {
				List<ItemDefinition> itemsList = Main.LocalPlayer.GetModPlayer<PlayerAssist>().BossItemsCollected;
				if (!itemsList.Any(x => x.Type == item.type)) {
					itemsList.Add(new ItemDefinition(item.type));
				}
			}
		}
		*/
	}
}
