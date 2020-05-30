using Microsoft.Xna.Framework;
using Mono.Data.Sqlite;
using MySql.Data.MySqlClient;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using Terraria;
using Terraria.Localization;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;
using TShockAPI.Hooks;

namespace DieMob
{
	public enum RegionType
	{
		Kill = 0,
		Repel = 1,
		Passive = 2
	}
	public class DieMobRegion
	{
        public Region TSRegion;
        public RegionType Type;
        public Dictionary<int, int> ReplaceMobs;
        public bool AffectFriendlyNPCs;
        public bool AffectStatueSpawns;

        public DieMobRegion(Region _reg)
        {
            TSRegion = _reg;
            Type = RegionType.Kill;
            ReplaceMobs = new Dictionary<int, int>();
            AffectFriendlyNPCs = false;
            AffectStatueSpawns = false;
        }
	}
	[ApiVersion(2,1)]
	public class DieMobMain : TerrariaPlugin
	{
		private static IDbConnection db;
		private static string savepath = Path.Combine(TShock.SavePath, "DieMob/");
		private static bool initialized = false;
		private static List<DieMobRegion> RegionList = new List<DieMobRegion>();
		private static DateTime lastUpdate = DateTime.UtcNow;
		private static Config config;
		// private static RegionManager regionManager;
		public override string Name
		{
			get { return "DieMob Regions"; }
		}
		public override string Author
		{
			get { return "Zaicon"; }
		}
		public override string Description
		{
			get { return "Adds monster protection option to regions"; }
		}
		public override Version Version
		{
			get { return Assembly.GetExecutingAssembly().GetName().Version; }
		}
		public DieMobMain(Main game)
			: base(game)
		{
			Order = 1;
		}
		public override void Initialize()
		{

			ServerApi.Hooks.GameUpdate.Register(this, OnUpdate);
			ServerApi.Hooks.GameInitialize.Register(this, OnInitialize, 1);
            RegionHooks.RegionDeleted += OnRegionDelete;

		}

        private void OnRegionDelete(RegionHooks.RegionDeletedEventArgs args)
        {
            if (RegionList.Exists(p => p.TSRegion.Name == args.Region.Name))
            {
                RegionList.RemoveAll(p => p.TSRegion.Name == args.Region.Name);
                db.Query($"DELETE FROM DieMob WHERE Region='{args.Region.Name}' AND WorldID={Main.worldID.ToString()};");
            }
        }

        protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				ServerApi.Hooks.GameInitialize.Deregister(this, OnInitialize);
				ServerApi.Hooks.GameUpdate.Deregister(this, OnUpdate);
                RegionHooks.RegionDeleted -= OnRegionDelete;
            }
			base.Dispose(disposing);
		}


		void OnInitialize(EventArgs e)
		{
			if (!Directory.Exists(savepath))
			{
				Directory.CreateDirectory(savepath);
				CreateConfig();
			}

			ReadConfig();

			Commands.ChatCommands.Add(new Command("diemob", DieMobCommand, "diemob", "DieMob", "dm"));

			switch (TShock.Config.StorageType.ToLower())
			{
				case "mysql":
					string[] dbHost = TShock.Config.MySqlHost.Split(':');
					db = new MySqlConnection()
					{
						ConnectionString = string.Format("Server={0}; Port={1}; Database={2}; Uid={3}; Pwd={4};",
							dbHost[0],
							dbHost.Length == 1 ? "3306" : dbHost[1],
							TShock.Config.MySqlDbName,
							TShock.Config.MySqlUsername,
							TShock.Config.MySqlPassword)

					};
					break;

				case "sqlite":
					string sql = Path.Combine(TShock.SavePath, "DieMob.sqlite");
					db = new SqliteConnection(string.Format("uri=file://{0},Version=3", sql));
					break;

			}

			SqlTableCreator sqlcreator = new SqlTableCreator(db, db.GetSqlType() == SqlType.Sqlite ? (IQueryBuilder)new SqliteQueryCreator() : new MysqlQueryCreator());

			sqlcreator.EnsureTableStructure(new SqlTable("DieMobRegions",
				new SqlColumn("ID", MySqlDbType.Int32) { Primary = true, Unique = true, AutoIncrement = true, Length = 6},
				new SqlColumn("Region", MySqlDbType.VarChar) { Length = 30 },
				new SqlColumn("WorldID", MySqlDbType.Int32),
				new SqlColumn("AffectFriendlyNPCs", MySqlDbType.Int32),
				new SqlColumn("AffectStatueSpawns", MySqlDbType.Int32),
				new SqlColumn("ReplaceMobs", MySqlDbType.Text),
				new SqlColumn("Type", MySqlDbType.Int32)));



		}

		class Config
		{
			public int UpdateInterval = 1000;
			public float RepelPowerModifier = 1.0f;
		}
		private static void CreateConfig()
		{
			string filepath = Path.Combine(savepath, "config.json");

			try
			{
				File.WriteAllText(filepath, JsonConvert.SerializeObject(new Config(), Formatting.Indented));
			}
			catch (Exception ex)
			{
				TShock.Log.ConsoleError(ex.Message);
				config = new Config();
			}
		}
		private static bool ReadConfig()
		{
			string filepath = Path.Combine(savepath, "config.json");
			try
			{
				if (File.Exists(filepath))
				{
					config = JsonConvert.DeserializeObject<Config>(File.ReadAllText(filepath));
					return true;
				}
				else
				{
					TShock.Log.ConsoleError("[DieMob] Configuração não encontrada. Criando uma nova");
					CreateConfig();
					return false;
				}
			}
			catch (Exception ex)
			{
			TShock.Log.ConsoleError(ex.Message);
			}
			return false;
		}

		private static void OnWorldLoad()
		{
			DieMob_Read();
		}
		private void OnUpdate(EventArgs e)
		{
			if ((DateTime.UtcNow - lastUpdate).TotalMilliseconds >= config.UpdateInterval)
			{
				lastUpdate = DateTime.UtcNow;
				if (!initialized && Main.worldID > 0)
				{
					initialized = true;
					OnWorldLoad();
				}
				try
				{
					for (int r = 0; r < RegionList.Count; r++)
					{
						Region reg = TShock.Regions.GetRegionByName(RegionList[r].TSRegion.Name);
						if (reg == null)
						{

							db.Query("DELETE FROM DieMobRegions WHERE Region=@0 AND WorldID=@1", RegionList[r].TSRegion.Name, Main.worldID);
							RegionList.RemoveAt(r);
							continue;
						}
						DieMobRegion Region = RegionList[r];
                        Region.TSRegion = reg;
						for (int i = 0; i < Main.npc.Length; i++)
						{
							if (Main.npc[i].active)
							{
								NPC npc = Main.npc[i];
								if (npc.friendly && Region.AffectFriendlyNPCs && npc.netID != 488) /*||*/
                                    //(!npc.friendly && npc.SpawnedFromStatue && Region.AffectStatueSpawns && npc.netID != 488 && npc.catchItem == 0) ||
                                    //(!npc.friendly && !npc.SpawnedFromStatue && npc.netID != 488 && npc.catchItem == 0))
								{
									if (Region.TSRegion.InArea((int)(Main.npc[i].position.X / 16), (int)(Main.npc[i].position.Y / 16)))
									{
										if (Region.ReplaceMobs.ContainsKey(npc.netID))
										{
											npc.SetDefaults(Region.ReplaceMobs[npc.netID]);
											NetMessage.SendData((int)PacketTypes.NpcUpdate, -1, -1, NetworkText.Empty, i);
										}
										else if (Region.ReplaceMobs.ContainsKey(-100))
										{
											npc.SetDefaults(Region.ReplaceMobs[-100]);
											NetMessage.SendData((int)PacketTypes.NpcUpdate, -1, -1, NetworkText.Empty, i);
										}
										else if (Region.Type == RegionType.Repel)
										{
											Rectangle area = Region.TSRegion.Area;
											int yDir = -10;
											if (area.Bottom - (int)(npc.position.Y / 16) < area.Height / 2)
												yDir = 10;
											int xDir = -10;
											if (area.Right - (int)(npc.position.X / 16) < area.Width / 2)
												xDir = 10;
											npc.velocity = new Vector2(xDir * config.RepelPowerModifier, yDir * config.RepelPowerModifier);
											NetMessage.SendData((int)PacketTypes.NpcUpdate, -1, -1, NetworkText.Empty, i);
										}
										else if (Region.Type == RegionType.Kill)
										{
											Main.npc[i] = new NPC();
											NetMessage.SendData((int)PacketTypes.NpcUpdate, -1, -1, NetworkText.Empty, i);
										}
									}
								}
							}
						}
					}

				}
				catch (Exception ex)
				{
					TShock.Log.ConsoleError(ex.ToString());
				}
			}
		}

		private static void DieMobCommand(CommandArgs args)
		{
			if (args.Parameters.Count > 0 && args.Parameters[0].ToLower() == "reload")
			{
				if (ReadConfig())
					args.Player.SendMessage("[DieMob] Configuração recarregada.", Color.BurlyWood);
				else
					args.Player.SendErrorMessage("[DieMob] Erro recarregando o arquivo de configuração.");
				return;
			}
			else if (args.Parameters.Count > 0 && args.Parameters[0].ToLower() == "list")
			{
				for (int r = 0; r < RegionList.Count; r++)
				{
					var regManReg = TShock.Regions.GetRegionByName(RegionList[r].TSRegion.Name);
					if (RegionList[r].TSRegion == null || regManReg == null || regManReg.Name == "")
					{
						db.Query("DELETE FROM DieMobRegions WHERE Region=@0 AND WorldID=@1", RegionList[r].TSRegion.Name, Main.worldID);
						RegionList.RemoveAt(r);
					}
				}

				int pageNumber;

				if (args.Parameters.Count < 2)
					pageNumber = 1;
				else if (!int.TryParse(args.Parameters[1], out pageNumber))
					args.Player.SendErrorMessage("[DieMob] Uso correto: {0}diemob list <página>", (args.Silent ? TShock.Config.CommandSilentSpecifier : TShock.Config.CommandSpecifier));

				if (!PaginationTools.TryParsePageNumber(args.Parameters, 1, args.Player, out pageNumber))
				{
					return;
				}
				IEnumerable<string> Regions =	from region in RegionList
												where region.TSRegion != null
												select string.Format("{0} @ X: {1}, Y: {2}", region.TSRegion.Name, region.TSRegion.Area.X,
												region.TSRegion.Area.Y);

				PaginationTools.SendPage(args.Player, pageNumber, PaginationTools.BuildLinesFromTerms(Regions),
					new PaginationTools.Settings
					{
						HeaderFormat = "Regiões do DieMob ({0}/{1}):",
						FooterFormat = "Digite /diemob list {0} para ver mais."
					});
				return;
			}
			else if (args.Parameters.Count > 1 && args.Parameters[0].ToLower() == "info")
			{
				DieMobRegion reg = GetRegionByName(args.Parameters[1]);
				if (reg == null)
					args.Player.SendMessage(String.Format("[DieMob] Região {0} não encontrada na lista", args.Parameters[1]), Color.Red);
				else
				{
					args.Player.SendMessage(String.Format("Região do DieMob: {0}", reg.TSRegion.Name), Color.DarkOrange);
					args.Player.SendMessage(String.Format("Tipo: {0}", reg.Type.ToString()), Color.LightSalmon);
					args.Player.SendMessage(String.Format("Afeta NPCs amigos: {0}", reg.AffectFriendlyNPCs ? "Sim" : "Não"), Color.LightSalmon);
					args.Player.SendMessage(String.Format("Afeta NPCs invocados através de Estatuas: {0}", reg.AffectStatueSpawns ? "Sim" : "Não"), Color.LightSalmon);
					args.Player.SendMessage(String.Format("Substituindo {0} mobs. Digite '{1}dm replacemobsinfo NomeDaRegião [Página]' para ver a lista de mobs.", reg.ReplaceMobs.Count, (args.Silent ? TShock.Config.CommandSilentSpecifier : TShock.Config.CommandSpecifier)), Color.LightSalmon);
				}
				return;
			}
			else if (args.Parameters.Count > 1 && (args.Parameters[0].ToLower() == "replacemobsinfo" || args.Parameters[0].ToLower() == "rminfo"))
			{
				DieMobRegion reg = GetRegionByName(args.Parameters[1]);
				if (reg == null)
					args.Player.SendErrorMessage("[DieMob] Região {0} não encontrada", args.Parameters[1]);
				else
				{
					int page = 0;
					if (args.Parameters.Count > 2)
						int.TryParse(args.Parameters[2], out page);
					if (page <= 0)
						page = 1;
					int startIndex = (page - 1) * 6;
					args.Player.SendMessage(String.Format("[DieMob] {0} mob em substituição: página {1}:", reg.TSRegion.Name, page), Color.LightSalmon);
					for (int i = startIndex; i < reg.ReplaceMobs.Count; i++)
					{
						if (i < startIndex + 6)
						{
							int key = reg.ReplaceMobs.Keys.ElementAt(i);
							args.Player.SendMessage(String.Format("[{0}] Antes: {1}  Depois: {2}", i + 1, key, reg.ReplaceMobs[key]), Color.BurlyWood);
						}
					}
				}
				return;
			}
			else if (args.Parameters.Count > 0 && args.Parameters[0].ToLower() == "mod")
			{
				if (args.Parameters.Count > 1)
				{
					DieMobRegion region = GetRegionByName(args.Parameters[1]);
					if (region == null)
					{
						args.Player.SendErrorMessage("[DieMob] Região {0} não encontrada", args.Parameters[1]);
						return;
					}
					if (args.Parameters.Count > 2)
					{
						switch (args.Parameters[2].ToLower())
						{
							case "type":
								{
									if (args.Parameters.Count > 3 && (args.Parameters[3].ToLower() == "kill" || args.Parameters[3].ToLower() == "repel" ||
										args.Parameters[3].ToLower() == "passive"))
									{
										if (args.Parameters[3].ToLower() == "repel")
										{
											region.Type = RegionType.Repel;
											args.Player.SendMessage(String.Format("[DieMob] A região {0} agora irá repelir os mobs", region.TSRegion.Name), Color.LightSalmon);
										}
										else if (args.Parameters[3].ToLower() == "passive")
										{
											region.Type = RegionType.Passive;
											args.Player.SendMessage(String.Format("[DieMob] A região {0} agora irá atuar com passiva", region.TSRegion.Name), Color.LightSalmon);
										}
										else
										{
											region.Type = RegionType.Kill;
											args.Player.SendMessage(String.Format("[DieMob] A região {0} agora irá matar os mobs", region.TSRegion.Name), Color.LightSalmon);
										}
										Diemob_Update(region);
										return;
									}
									break;
								}
							case "affectfriendlynpcs":
								{
									if (args.Parameters.Count > 3 && (args.Parameters[3].ToLower() == "true" || args.Parameters[3].ToLower() == "false"))
									{
										if (args.Parameters[3].ToLower() == "true")
										{
											region.AffectFriendlyNPCs = true;
											args.Player.SendMessage(String.Format("[DieMob] A Região {0} agora afeta NPCs amigos", region.TSRegion.Name),
												Color.LightSalmon);
										}
										else
										{
											region.AffectFriendlyNPCs = false;
											args.Player.SendMessage(String.Format("[DieMob] A Região {0} agora não afeta mais NPCs amigos", region.TSRegion.Name),
												Color.LightSalmon);
										}
										Diemob_Update(region);
										return;
									}
									break;
								}
							case "affectstatuespawns":
								{
									if (args.Parameters.Count > 3 && (args.Parameters[3].ToLower() == "true" || args.Parameters[3].ToLower() == "false"))
									{
										if (args.Parameters[3].ToLower() == "true")
										{
											region.AffectStatueSpawns = true;
											args.Player.SendMessage(String.Format("[DieMob] A Região {0} agora afeta a invocação de mobs por estátuas", region.TSRegion.Name),
												Color.LightSalmon);
										}
										else
										{
											region.AffectStatueSpawns = false;
											args.Player.SendMessage(String.Format("[DieMob] A Região {0} agora não afeta a invocação de mobs por estátuas", region.TSRegion.Name),
												Color.LightSalmon);
										}
										Diemob_Update(region);
										return;
									}
									break;
								}
							case "replacemobs":
								{
									if (args.Parameters.Count > 4 && (args.Parameters[3].ToLower() == "add" || args.Parameters[3].ToLower() == "del"))
									{
										int fromMobID;
										int toMobID;
										if (args.Parameters[3].ToLower() == "add" && args.Parameters.Count > 5 && int.TryParse(args.Parameters[4], out fromMobID) &&
											int.TryParse(args.Parameters[5], out toMobID))
										{
											if (region.ReplaceMobs.ContainsKey(fromMobID))
											{
												args.Player.SendMessage(String.Format("[DieMob] A Região {0} já está convertendo o mob {1} em {2}",
													region.TSRegion.Name, fromMobID, region.ReplaceMobs[fromMobID]), Color.LightSalmon);
												return;
											}
											region.ReplaceMobs.Add(fromMobID, toMobID);
											args.Player.SendMessage(String.Format("[DieMob] A Região {0} agora está convertendo o mob {1} em {2}", region.TSRegion.Name,
												fromMobID, toMobID), Color.LightSalmon);
											Diemob_Update(region);
											return;
										}
										else if (args.Parameters[3].ToLower() == "del" && int.TryParse(args.Parameters[4], out fromMobID))
										{
											if (region.ReplaceMobs.ContainsKey(fromMobID))
												region.ReplaceMobs.Remove(fromMobID);
											args.Player.SendMessage(String.Format("[DieMob] A Região {0} agora não está mais convertendo mob com a ID {1}", region.TSRegion.Name, fromMobID),
												Color.LightSalmon);
											Diemob_Update(region);
											return;
										}
									}
									break;
								}
						}
					}
				}
				args.Player.SendMessage("{0}dm mod NomeDaRegião Opção Argumentos".SFormat(args.Silent ? TShock.Config.CommandSilentSpecifier : TShock.Config.CommandSpecifier), Color.DarkOrange);
				args.Player.SendMessage("Opções:", Color.LightSalmon);
				args.Player.SendMessage("Tipo - args: kill [default] / repel / passive", Color.LightSalmon);
				args.Player.SendMessage("affectfriendlynpcs - args: true / false [default]", Color.LightSalmon);
				args.Player.SendMessage("affectstatuespawns - args: true / false [default]", Color.LightSalmon);
				args.Player.SendMessage("replacemobs - args: add fromMobID toMobID / del fromMobID", Color.LightSalmon);
				return;
			}
			else if (args.Parameters.Count > 1)
			{
				var region = TShock.Regions.GetRegionByName(args.Parameters[1]);
				if (region != null && region.Name != "")
				{
					if (args.Parameters[0].ToLower() == "add")
					{
						if (RegionList.Select(r => r.TSRegion).Contains(region))
						{
							args.Player.SendMessage(String.Format("[DieMob] A Região '{0}' já está na lista", region.Name), Color.LightSalmon);
							return;
						}
						if (!DieMob_Add(region.Name))
						{
							args.Player.SendErrorMessage("[DieMob] Erro ao adicionar '{0}' a lista.", region.Name);
							return;
						}
                        RegionList.Add(new DieMobRegion(region));
						args.Player.SendMessage(String.Format("[DieMob] A Região '{0}' foi adicionada a lista", region.Name), Color.BurlyWood);
						return;
					}
					else if (args.Parameters[0].ToLower() == "del")
					{
						if (!RegionList.Exists(r => r.TSRegion.Name == region.Name))
						{
							args.Player.SendMessage(String.Format("[DieMob] A Região '{0}' não está na lista", region.Name), Color.LightSalmon);
							return;
						}
						DieMob_Delete(region.Name);
						args.Player.SendMessage(String.Format("[DieMob] A Região '{0}' foi excluída da lista", region.Name), Color.BurlyWood);
						return;
					}
					return;
				}
				else
				{
					args.Player.SendErrorMessage($"[DieMob] A Região '{args.Parameters[1]}' não foi encontrada.");
					return;
				}
			}
			args.Player.SendMessage("Syntax: {0}diemob [add | del] RegionName - Criar / Deleta uma Região baseada em uma Região existente".SFormat(args.Silent ? TShock.Config.CommandSilentSpecifier : TShock.Config.CommandSpecifier), Color.LightSalmon);
			args.Player.SendMessage("Syntax: {0}diemob list [page number] - Lista as Regiões do DieMob".SFormat(args.Silent ? TShock.Config.CommandSilentSpecifier : TShock.Config.CommandSpecifier), Color.LightSalmon);
			args.Player.SendMessage("Syntax: {0}diemob reload - Recarrega a config.json".SFormat(args.Silent ? TShock.Config.CommandSilentSpecifier : TShock.Config.CommandSpecifier), Color.LightSalmon);
			args.Player.SendMessage("Syntax: {0}diemob mod RegionName - Modifica uma Região DieMob".SFormat(args.Silent ? TShock.Config.CommandSilentSpecifier : TShock.Config.CommandSpecifier), Color.LightSalmon);
			args.Player.SendMessage("Syntax: {0}diemob info RegionName - Mostra informações sobre uma Região DieMob".SFormat(args.Silent ? TShock.Config.CommandSilentSpecifier : TShock.Config.CommandSpecifier), Color.LightSalmon);
		}
		private static void DieMob_Read()
		{
			QueryResult reader;

			reader = db.QueryReader("SELECT * FROM DieMobRegions WHERE WorldID=@0", Main.worldID);
			List<string> obsoleteRegions = new List<string>();
			while (reader.Read())
			{
				var regionName = reader.Get<string>("Region");
				var region = TShock.Regions.GetRegionByName(regionName);
				if (region != null && region.Name != "")
				{
					RegionList.Add(new DieMobRegion(region)
					{
						AffectFriendlyNPCs = reader.Get<bool>("AffectFriendlyNPCs"),
						AffectStatueSpawns = reader.Get<bool>("AffectStatueSpawns"),
						ReplaceMobs = JsonConvert.DeserializeObject<Dictionary<int, int>>(reader.Get<string>("ReplaceMobs")),
						Type = (RegionType)reader.Get<int>("Type")
					});
				}
				else
				{
					obsoleteRegions.Add(regionName);
				}
			}
			reader.Dispose();
			foreach (string region in obsoleteRegions)
			{
				Console.WriteLine("Deleting region from DB: " + region);
				db.Query("DELETE FROM DieMobRegions WHERE Region=@0 AND WorldID=@1", region, Main.worldID);

			}
		}
		private static bool DieMob_Add(string name)
		{
			db.Query("INSERT INTO DieMobRegions (Region, WorldID, AffectFriendlyNPCs, AffectStatueSpawns, Type, ReplaceMobs) VALUES (@0, @1, 0, 0, 0, @2)",
				name, Main.worldID, JsonConvert.SerializeObject(new Dictionary<int, int>()));
			return true;
		}
		private static void DieMob_Delete(String name)
		{
			db.Query("DELETE FROM DieMobRegions WHERE Region=@0 AND WorldID=@1", name, Main.worldID);
			for (int i = 0; i < RegionList.Count; i++)
			{
				if (RegionList[i].TSRegion.Name == name)
				{

                    RegionList.RemoveAt(i);
					break;
				}
			}
		}
		private static void Diemob_Update(DieMobRegion region)
		{
			db.Query("UPDATE DieMobRegions SET AffectFriendlyNPCs=@2, AffectStatueSpawns=@3, Type=@4, ReplaceMobs=@5 WHERE Region=@0 AND WorldID=@1",
				region.TSRegion.Name, Main.worldID, region.AffectFriendlyNPCs, region.AffectStatueSpawns,
				(int)region.Type, JsonConvert.SerializeObject(region.ReplaceMobs));
		}

		private static DieMobRegion GetRegionByName(string name)
		{
			foreach (DieMobRegion reg in RegionList)
			{
				if (reg.TSRegion.Name == name)
					return reg;
			}
			return null;
		}
	}


}
