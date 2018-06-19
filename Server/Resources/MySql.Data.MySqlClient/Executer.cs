﻿using Microsoft.Extensions.Logging;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace MySql.Data.MySqlClient {
	public partial class Executer : IDisposable {

		public bool IsTracePerformance { get; set; } = string.Compare(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", true) == 0;
		public static ILogger LogStatic;
		public ILogger Log { get { return LogStatic; } set { LogStatic = value; } }
		public ConnectionPool Pool { get; }
		public Executer() { }
		public Executer(ILogger log, string connectionString) {
			this.Log = log;
			this.Pool = new ConnectionPool(connectionString);
		}

		void LoggerException(MySqlCommand cmd, Exception e, DateTime dt, string logtxt) {
			if (IsTracePerformance) {
				TimeSpan ts = DateTime.Now.Subtract(dt);
				if (e == null && ts.TotalMilliseconds > 100)
					Log.LogWarning($"执行SQL语句耗时过长{ts.TotalMilliseconds}ms\r\n{cmd.CommandText}\r\n{logtxt}");
			}

			if (e == null) return;
			string log = $"数据库出错（执行SQL）〓〓〓〓〓〓〓〓〓〓〓〓〓〓〓\r\n{cmd.CommandText}\r\n";
			foreach (MySqlParameter parm in cmd.Parameters)
				log += Lib.PadRight(parm.ParameterName, 20) + " = " + Lib.PadRight(parm.Value ?? "NULL", 20) + "\r\n";

			log += e.Message;
			Log.LogError(log);

			RollbackTransaction();
			cmd.Parameters.Clear();
			throw e;
		}

		public static string Addslashes(string filter, params object[] parms) {
			if (filter == null || parms == null) return string.Empty;
			if (parms.Length == 0) return filter;
			object[] nparms = new object[parms.Length];
			for (int a = 0; a < parms.Length; a++) {
				if (parms[a] == null) nparms[a] = "NULL";
				else {
					decimal trydec;
					if (parms[a] is bool || parms[a] is bool?)
						nparms[a] = (bool)parms[a] ? 1 : 0;
					else if (parms[a] is string)
						nparms[a] = string.Concat("'", parms[a].ToString().Replace("'", "''"), "'");
					else if (parms[a] is Enum)
						nparms[a] = ((Enum)parms[a]).ToInt64();
					else if (decimal.TryParse(string.Concat(parms[a]), out trydec))
						nparms[a] = parms[a];
					else if (parms[a] is DateTime) {
						DateTime dt = (DateTime)parms[a];
						nparms[a] = string.Concat("'", dt.ToString("yyyy-MM-dd HH:mm:ss"), "'");
					} else if (parms[a] is DateTime?) {
						DateTime? dt = parms[a] as DateTime?;
						nparms[a] = string.Concat("'", dt.Value.ToString("yyyy-MM-dd HH:mm:ss"), "'");
					} else if (parms[a] is IEnumerable) {
						string sb = "";
						var ie = parms[a] as IEnumerable;
						foreach (var z in ie) sb += z == null ? string.Concat(",NULL") : string.Concat(",'", z.ToString().Replace("'", "''"), "'");
						nparms[a] = string.IsNullOrEmpty(sb) ? sb : sb.Substring(1);
					} else {
						nparms[a] = string.Concat("'", parms[a].ToString().Replace("'", "''"), "'");
						//if (parms[a] is string) nparms[a] = string.Concat('N', nparms[a]);
					}
				}
			}
			try { string ret = string.Format(filter, nparms); return ret; } catch { return filter; }
		}
		public void ExecuteReader(Action<MySqlDataReader> readerHander, CommandType cmdType, string cmdText, params MySqlParameter[] cmdParms) {
			DateTime dt = DateTime.Now;
			MySqlCommand cmd = new MySqlCommand();
			string logtxt = "";
			DateTime logtxt_dt = DateTime.Now;
			var pc = PrepareCommand(cmd, cmdType, cmdText, cmdParms, ref logtxt);
			if (IsTracePerformance) logtxt += $"PrepareCommand: {DateTime.Now.Subtract(logtxt_dt).TotalMilliseconds}ms Total: {DateTime.Now.Subtract(dt).TotalMilliseconds}ms\r\n";
			Exception ex = null;
			try {
				if (IsTracePerformance) logtxt_dt = DateTime.Now;
				if (cmd.Connection.State == ConnectionState.Closed || cmd.Connection.Ping() == false) cmd.Connection.Open();
				if (IsTracePerformance) {
					logtxt += $"Open: {DateTime.Now.Subtract(logtxt_dt).TotalMilliseconds}ms Total: {DateTime.Now.Subtract(dt).TotalMilliseconds}ms\r\n";
					logtxt_dt = DateTime.Now;
				}
				using (MySqlDataReader dr = cmd.ExecuteReader()) {
					if (IsTracePerformance) logtxt += $"ExecuteReader: {DateTime.Now.Subtract(logtxt_dt).TotalMilliseconds}ms Total: {DateTime.Now.Subtract(dt).TotalMilliseconds}ms\r\n";
					while (true) {
						if (IsTracePerformance) logtxt_dt = DateTime.Now;
						bool isread = dr.Read();
						if (IsTracePerformance) logtxt += $"	dr.Read: {DateTime.Now.Subtract(logtxt_dt).TotalMilliseconds}ms Total: {DateTime.Now.Subtract(dt).TotalMilliseconds}ms\r\n";
						if (isread == false) break;

						if (readerHander != null) {
							object[] values = null;
							if (IsTracePerformance) {
								logtxt_dt = DateTime.Now;
								values = new object[dr.FieldCount];
								dr.GetValues(values);
								logtxt += $"	dr.GetValues: {DateTime.Now.Subtract(logtxt_dt).TotalMilliseconds}ms Total: {DateTime.Now.Subtract(dt).TotalMilliseconds}ms\r\n";
								logtxt_dt = DateTime.Now;
							}
							readerHander(dr);
							if (IsTracePerformance) logtxt += $"	readerHander: {DateTime.Now.Subtract(logtxt_dt).TotalMilliseconds}ms Total: {DateTime.Now.Subtract(dt).TotalMilliseconds}ms ({string.Join(",", values)})\r\n";
						}
					}
					if (IsTracePerformance) logtxt_dt = DateTime.Now;
					dr.Close();
				}
				if (IsTracePerformance) logtxt += $"ExecuteReader_dispose: {DateTime.Now.Subtract(logtxt_dt).TotalMilliseconds}ms Total: {DateTime.Now.Subtract(dt).TotalMilliseconds}ms\r\n";
			} catch (Exception ex2) {
				ex = ex2;
			}

			if (pc.Tran == null) {
				if (IsTracePerformance) logtxt_dt = DateTime.Now;
				this.Pool.ReleaseConnection(pc.Conn);
				if (IsTracePerformance) logtxt += $"ReleaseConnection: {DateTime.Now.Subtract(logtxt_dt).TotalMilliseconds}ms Total: {DateTime.Now.Subtract(dt).TotalMilliseconds}ms";
			}
			LoggerException(cmd, ex, dt, logtxt);
		}
		public object[][] ExeucteArray(CommandType cmdType, string cmdText, params MySqlParameter[] cmdParms) {
			List<object[]> ret = new List<object[]>();
			ExecuteReader(dr => {
				object[] values = new object[dr.FieldCount];
				dr.GetValues(values);
				ret.Add(values);
			}, cmdType, cmdText, cmdParms);
			return ret.ToArray();
		}
		public int ExecuteNonQuery(CommandType cmdType, string cmdText, params MySqlParameter[] cmdParms) {
			DateTime dt = DateTime.Now;
			MySqlCommand cmd = new MySqlCommand();
			string logtxt = "";
			DateTime logtxt_dt = DateTime.Now;
			var pc = PrepareCommand(cmd, cmdType, cmdText, cmdParms, ref logtxt);
			int val = 0;
			Exception ex = null;
			try {
				if (cmd.Connection.State == ConnectionState.Closed || cmd.Connection.Ping() == false) cmd.Connection.Open();
				val = cmd.ExecuteNonQuery();
			} catch (Exception ex2) {
				ex = ex2;
			}

			if (pc.Tran == null) {
				if (IsTracePerformance) logtxt_dt = DateTime.Now;
				this.Pool.ReleaseConnection(pc.Conn);
				if (IsTracePerformance) logtxt += $"ReleaseConnection: {DateTime.Now.Subtract(logtxt_dt).TotalMilliseconds}ms Total: {DateTime.Now.Subtract(dt).TotalMilliseconds}ms";
			}
			LoggerException(cmd, ex, dt, logtxt);
			cmd.Parameters.Clear();
			return val;
		}
		public object ExecuteScalar(CommandType cmdType, string cmdText, params MySqlParameter[] cmdParms) {
			DateTime dt = DateTime.Now;
			MySqlCommand cmd = new MySqlCommand();
			string logtxt = "";
			DateTime logtxt_dt = DateTime.Now;
			var pc = PrepareCommand(cmd, cmdType, cmdText, cmdParms, ref logtxt);
			object val = null;
			Exception ex = null;
			try {
				if (cmd.Connection.State == ConnectionState.Closed || cmd.Connection.Ping() == false) cmd.Connection.Open();
				val = cmd.ExecuteScalar();
			} catch (Exception ex2) {
				ex = ex2;
			}

			if (pc.Tran == null) {
				if (IsTracePerformance) logtxt_dt = DateTime.Now;
				this.Pool.ReleaseConnection(pc.Conn);
				if (IsTracePerformance) logtxt += $"ReleaseConnection: {DateTime.Now.Subtract(logtxt_dt).TotalMilliseconds}ms Total: {DateTime.Now.Subtract(dt).TotalMilliseconds}ms";
			}
			LoggerException(cmd, ex, dt, logtxt);
			cmd.Parameters.Clear();
			return val;
		}

		private PrepareCommandReturnInfo PrepareCommand(MySqlCommand cmd, CommandType cmdType, string cmdText, MySqlParameter[] cmdParms, ref string logtxt) {
			DateTime dt = DateTime.Now;
			cmd.CommandType = cmdType;
			cmd.CommandText = cmdText;

			if (cmdParms != null) {
				foreach (MySqlParameter parm in cmdParms) {
					if (parm == null) continue;
					if (parm.Value == null) parm.Value = DBNull.Value;
					cmd.Parameters.Add(parm);
				}
			}

			SqlConnection2 conn = null;
			MySqlTransaction tran = CurrentThreadTransaction;
			if (IsTracePerformance) logtxt += $"	PrepareCommand_part1: {DateTime.Now.Subtract(dt).TotalMilliseconds}ms cmdParms: {cmdParms.Length}\r\n";

			if (tran == null) {
				if (IsTracePerformance) dt = DateTime.Now;
				conn = this.Pool.GetConnection();
				cmd.Connection = conn.SqlConnection;
				if (IsTracePerformance) logtxt += $"	PrepareCommand_tran==null: {DateTime.Now.Subtract(dt).TotalMilliseconds}ms\r\n";
			} else {
				if (IsTracePerformance) dt = DateTime.Now;
				cmd.Connection = tran.Connection;
				cmd.Transaction = tran;
				if (IsTracePerformance) logtxt += $"	PrepareCommand_tran!=null: {DateTime.Now.Subtract(dt).TotalMilliseconds}ms\r\n";
			}

			if (IsTracePerformance) dt = DateTime.Now;
			AutoCommitTransaction();
			if (IsTracePerformance) logtxt += $"	AutoCommitTransaction: {DateTime.Now.Subtract(dt).TotalMilliseconds}ms\r\n";

			return new PrepareCommandReturnInfo { Conn = conn, Tran = tran };
		}
		
		class PrepareCommandReturnInfo {
			public SqlConnection2 Conn;
			public MySqlTransaction Tran;
		}

		#region 事务处理

		class SqlTransaction2 {
			internal SqlConnection2 Conn;
			internal MySqlTransaction Transaction;
			internal DateTime RunTime;
			internal TimeSpan Timeout;

			public SqlTransaction2(SqlConnection2 conn, MySqlTransaction tran, TimeSpan timeout) {
				Conn = conn;
				Transaction = tran;
				RunTime = DateTime.Now;
				Timeout = timeout;
			}
		}

		private Dictionary<int, SqlTransaction2> _trans = new Dictionary<int, SqlTransaction2>();
		private object _trans_lock = new object();

		public MySqlTransaction CurrentThreadTransaction => _trans.TryGetValue(Thread.CurrentThread.ManagedThreadId, out var conn) && conn.Transaction?.Connection != null ? conn.Transaction : null;

		private Dictionary<int, List<string>> _preRemoveKeys = new Dictionary<int, List<string>>();
		private object _preRemoveKeys_lock = new object();
		public string[] PreRemove(params string[] key) {
			var tid = Thread.CurrentThread.ManagedThreadId;
			List<string> keys = null;
			if (key == null || key.Any() == false) return _preRemoveKeys.TryGetValue(tid, out keys) ? keys.ToArray() : new string[0];
			this.Log.LogDebug($"线程{tid}事务预删除Redis {Newtonsoft.Json.JsonConvert.SerializeObject(key)}");
			if (_preRemoveKeys.TryGetValue(tid, out keys) == false)
				lock (_preRemoveKeys_lock)
					if (_preRemoveKeys.TryGetValue(tid, out keys) == false) {
						_preRemoveKeys.Add(tid, keys = new List<string>(key));
						return key;
					}
			keys.AddRange(key);
			return keys.ToArray();
		}

		/// <summary>
		/// 启动事务
		/// </summary>
		public void BeginTransaction() {
			BeginTransaction(TimeSpan.FromSeconds(10));
		}
		public void BeginTransaction(TimeSpan timeout) {
			int tid = Thread.CurrentThread.ManagedThreadId;
			var conn = this.Pool.GetConnection();
			SqlTransaction2 tran = null;

			Exception ex = Lib.Trys(delegate () {
				if (conn.SqlConnection.Ping() == false) conn.SqlConnection.Open();
				tran = new SqlTransaction2(conn, conn.SqlConnection.BeginTransaction(), timeout);
			}, 1);
			if (ex != null) {
				Log.LogError($"数据库出错（开启事务）{ex.Message} \r\n{ex.StackTrace}");
				throw ex;
			}
			if (_trans.ContainsKey(tid)) CommitTransaction();

			lock (_trans_lock)
				_trans.Add(tid, tran);
		}

		/// <summary>
		/// 自动提交事务
		/// </summary>
		private void AutoCommitTransaction() {
			if (_trans.Count > 0) {
				SqlTransaction2[] trans = null;
				lock (_trans_lock)
					trans = _trans.Values.Where(st2 => DateTime.Now.Subtract(st2.RunTime) > st2.Timeout).ToArray();
				foreach (SqlTransaction2 tran in trans) CommitTransaction(true, tran);
			}
		}
		private void CommitTransaction(bool isCommit, SqlTransaction2 tran) {
			if (tran == null || tran.Transaction == null || tran.Transaction.Connection == null) return;

			if (_trans.ContainsKey(tran.Conn.ThreadId))
				lock (_trans_lock)
					if (_trans.ContainsKey(tran.Conn.ThreadId))
						_trans.Remove(tran.Conn.ThreadId);

			var removeKeys = PreRemove().Distinct().ToArray();
			if (_preRemoveKeys.ContainsKey(tran.Conn.ThreadId))
				lock (_preRemoveKeys_lock)
					if (_preRemoveKeys.ContainsKey(tran.Conn.ThreadId))
						_preRemoveKeys.Remove(tran.Conn.ThreadId);

			var f001 = isCommit ? "提交" : "回滚";
			try {
				this.Log.LogDebug($"线程{tran.Conn.ThreadId}事务{f001}，批量删除Redis {Newtonsoft.Json.JsonConvert.SerializeObject(removeKeys)}");
				CSRedis.QuickHelperBase.Remove(removeKeys);
				if (isCommit) tran.Transaction.Commit();
				else tran.Transaction.Rollback();
			} catch (Exception ex) {
				Executer.LogStatic.LogError($"数据库出错（{f001}事务）：{ex.Message} {ex.StackTrace}");
			} finally {
				this.Pool.ReleaseConnection(tran.Conn);
			}
		}
		private void CommitTransaction(bool isCommit) {
			if (_trans.TryGetValue(Thread.CurrentThread.ManagedThreadId, out var tran)) CommitTransaction(isCommit, tran);
		}
		/// <summary>
		/// 提交事务
		/// </summary>
		public void CommitTransaction() => CommitTransaction(true);
		/// <summary>
		/// 回滚事务
		/// </summary>
		public void RollbackTransaction() => CommitTransaction(false);

		public void Dispose() {
			SqlTransaction2[] trans = null;
			lock (_trans_lock)
				trans = _trans.Values.ToArray();
			foreach (SqlTransaction2 tran in trans) CommitTransaction(false, tran);
		}
		#endregion
	}

	public static partial class ExtensionMethods {
		public static object GetEnum<T>(this IDataReader dr, int index) {
			string value = dr.GetString(index);
			Type t = typeof(T);
			foreach (var f in t.GetFields())
				if (f.GetCustomAttribute<DescriptionAttribute>()?.Description == value || f.Name == value) return Enum.Parse(t, f.Name);
			return null;
		}
	}
}

public static partial class MySql_Data_MySqlClient_ExtensionMethods {
	public static string ToDescriptionOrString(this Enum item) {
		string name = item.ToString();
		DescriptionAttribute desc = item.GetType().GetField(name)?.GetCustomAttribute<DescriptionAttribute>();
		return desc?.Description ?? name;
	}
	public static long ToInt64(this Enum item) {
		return Convert.ToInt64(item);
	}
	public static IEnumerable<T> ToSet<T>(this long value) {
		List<T> ret = new List<T>();
		if (value == 0) return ret;
		Type t = typeof(T);
		foreach (FieldInfo f in t.GetFields()) {
			if (f.FieldType != t) continue;
			object o = Enum.Parse(t, f.Name);
			long v = (long)o;
			if ((value & v) == v) ret.Add((T)o);
		}
		return ret;
	}
}