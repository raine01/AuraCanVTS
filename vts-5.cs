using WebSocketSharp;
using System.Windows.Forms;
using Triggernometry;
using Triggernometry.Variables;
using System.Text.RegularExpressions;
using static Triggernometry.Interpreter;
using System.Collections.Generic;
using System;
using System.Linq;
using System.Threading;

public const string wsUrl = "ws://127.0.0.1:8001"; // API地址
public const string pluginName = "AuraCanVTS"; // 插件名称，同时也是日志关键词
public const string developer = "WhisperSongs"; // 插件作者
// 插件图标 pluginIcon
AuraCanVTS.Init("abcde");

public static class AuraCanVTS {
	private static Schedular _schedular;//定时任务
	private static Dictionary<string, Handle> _handles;//日志行处理器列表
	private static VariableDictionary _dv;//持久变量
	private static string _playerId;//当前玩家Id

	//被用作回调函数,仅处理start/stop/press
	private static void Sender(string type, string command)
	{
		Dictionary<string, object> strAndPar = CheckCmd(type, command);
		Dictionary<string, object> parameters = (Dictionary<string, object>)strAndPar["parameters"];
		string longCmdStr = (string)strAndPar["longCmdStr"];
		Message msg = new Message(parameters);
		AuraCanSocket.SendToVTubeStudio(msg.Serialize());
		Logger.Log2(longCmdStr);
	}

	//该方法仅处理99行以内的网络日志
	public static void LogProcess(object _, string logString)
	{
		string handleNo = logString.Substring(0, 2);
		//默语指令处理
		if(handleNo == "00" && logString.Substring(37, 4) == "0038" && logString.Substring(43, 4) == "vts ")
		{
			string commandStr = logString.Substring(43, logString.LastIndexOf('|') - 43);//原始指令
			string longCommandStr = "";//处理后的指令(长)
			string shortCommandStr = "";//处理后的指令(短)
			Dictionary<string, object> parameters;//处理后的参数
			CommandTypeEnum commandType = CheckCommand(commandStr, out longCommandStr, out shortCommandStr,out parameters);
			Logger.Log2($"commandType:{commandType.ToString()}");
			Logger.Log($"command:{longCommandStr}");
			switch (commandType) {
				case CommandTypeEnum.createparameter://创建新参数
				case CommandTypeEnum.deleteparameter://删除参数
				case CommandTypeEnum.executecommand://执行指令
					AuraCanSocket.SendToVTubeStudio(new Message(parameters).Serialize());
					break;
				case CommandTypeEnum.createcommand://创建指令
					VtsCreateCommand(shortCommandStr, parameters);
					break;
				case CommandTypeEnum.deletecommand://删除指令
					VtsDeleteCommand(parameters);
					break;
				case CommandTypeEnum.showcommand://列出指令
					VtsShowCommand();
					break;
				case CommandTypeEnum.schedular://定时器
					VtsCreateSchedular(shortCommandStr, parameters);
					break;
				case CommandTypeEnum.changeLog://改变日志级别
					Logger.SetLogger((string)parameters["level"]);
					break;
				case CommandTypeEnum.donothing:
					Logger.Log("啥也不干");
					break; //无匹配
			}
		}
		else if(_handles.ContainsKey(handleNo))//其他日志处理(非默语)
		{
			logString = logString.Substring(37);
			string[] log = logString.Split('|');
			_handles[handleNo].HandleProcess(_playerId, log);
		}
		if(handleNo == "02")//更新当前玩家的名称和ID(可能会有更换模型的动作,因此不作为elseif)
		{
			_playerId = logString.Split('|')[2];
			StaticHelpers.SetScalarVariable(true, $"{pluginName}PlayerId", _playerId);
			Logger.Log2($"player {_playerId}");
		}
	}

	//把_handles重新初始化就不会再发请求了
	public static void DestoryHandles()
	{
		_handles = new Dictionary<string, Handle>();
	}

	public static void Init(string steps)
	{
		//初始化类
		if(steps.Contains("a"))
		{
			_handles = new Dictionary<string, Handle>();//key为10进制
			string logLevel = StaticHelpers.GetScalarVariable(true, $"{pluginName}LogLevel") ?? "0";//日志级别,默认0
			Logger.SetLogger(logLevel);
			_playerId = StaticHelpers.GetScalarVariable(true, $"{pluginName}PlayerId") ?? "";
			Logger.Log2($"init a finished,playerId is {_playerId}");
		}
		//初始化websocket链接
		if(steps.Contains("b"))
		{
			AuraCanSocket.InitSocket();
			Logger.Log2($"init b finished");
		}
		//初始化定时器
		if(steps.Contains("c"))
		{
			_schedular = new Schedular();
			Logger.Log2($"init c finished");
		}
		//字符串还原回方法
		if(steps.Contains("d"))
		{
			_dv = StaticHelpers.GetDictVariable(true, pluginName);
			if(_dv == null)
			{
				StaticHelpers.SetDictVariable(true, pluginName, new VariableDictionary());
				_dv = StaticHelpers.GetDictVariable(true, pluginName);
			}
			else
			{
				foreach(var v in _dv.Values.Values)
				{
					string commandStr = v.ToString();
					if(Regex.Match(commandStr, @"^vts set").Success) //set
					{
						Logger.Log2($"init schedular:{commandStr}");
						CheckCommand(commandStr, out _, out _,out Dictionary<string, object> parameters);
						VtsCreateSchedular("", parameters);
					}
					else //start/stop/press
					{
						Logger.Log2($"init command:{commandStr}");
						CheckCommand(commandStr, out _, out _,out Dictionary<string, object> parameters);
						VtsCreateCommand("", parameters);
					}
				}
				Logger.Log2($"rebuild {_dv.Size} command(s)");
			}
			Logger.Log2($"init d finished");
		}
		//注册回调函数
		if(steps.Contains("e"))
		{
			RealPlugin.plug.RegisterNamedCallback($"{pluginName}", new Action<object, string>(LogProcess), null);
			RealPlugin.plug.RegisterNamedCallback($"{pluginName}StartSender", (object _, string command) => Sender("start", command), null);
			RealPlugin.plug.RegisterNamedCallback($"{pluginName}StopSender", (object _, string command) => Sender("stop", command), null);
			RealPlugin.plug.RegisterNamedCallback($"{pluginName}PressSender", (object _, string command) => Sender("press", command), null);
			RealPlugin.plug.RegisterNamedCallback($"{pluginName}JsonSender", (object _, string json) => AuraCanSocket.SendToVTubeStudio(json), null);//究极摆烂
			Logger.Log2($"init e finished");
		}
		Logger.Log2($"init finished");
	}

	private static CommandTypeEnum CheckCommand(string commandStr, out string longCommandStr, out string shortCommandStr, out Dictionary<string, object> parameters)
	{
		Match match;
		parameters = new Dictionary<string, object>();
		/*
			CommandTypeEnum.createparameter:
				Parameter names have to be unique, alphanumeric (no spaces allowed) and have to be between 4 and 32 characters in length.
			eg:
				vts create new parameter ParamBodyAngleX with maximum value 20, minimum value 10, default value 15, and notes that the parameter is optional
				vts create new parameter ParamBodyAngleX with maximum value 20, minimum value 10, default value 15
				vts new par ParamBodyAngleX 20 10 15 note the parameter is optional
				vts new par ParamBodyAngleX 20 10 15
		*/
		match = Regex.Match(commandStr, @"^vts(?:\s+create)?\s+new\s+par(?:ameter)?\s+(\w+)(?: with maximum value)?\s+(-?\d+)(?:, minimum value)?\s+(-?\d+)(?:, default value)?\s+(-?\d+)(?:, and notes that)?(?:\s+(.{0,255}))?$");
		if(match.Success)
		{
			string parameterName = match.Groups[1].Value;

			double[] _values = {
				double.Parse(match.Groups[2].Value),
				double.Parse(match.Groups[3].Value),
				double.Parse(match.Groups[4].Value)
			};
			Array.Sort(_values);
			string min = _values[0].ToString();
			string max = _values[2].ToString();
			string defaultValue = _values[1].ToString();

			longCommandStr = $"vts create new parameter {parameterName} with maximum value {max}, minimum value {min}, default value {defaultValue}";
			shortCommandStr = $"vts new par {parameterName} {max} {min} {defaultValue}";

			parameters["parameterName"] = parameterName;
			parameters["max"] = max;
			parameters["min"] = min;
			parameters["defaultValue"] = defaultValue;
			parameters["type"] = MessageTypeEnum.ParameterCreationRequest;

			if(!"".Equals(match.Groups[5]))//有备注
			{
				string explanation = match.Groups[5].Value;
				longCommandStr = $"{longCommandStr}, and notes that {explanation}";
				shortCommandStr = $"{shortCommandStr} {explanation}";
				parameters["explanation"] = explanation;
			}
			return CommandTypeEnum.createparameter;
		}

		/*
			CommandTypeEnum.deleteparameter:
				Parameter names have to be unique, alphanumeric (no spaces allowed) and have to be between 4 and 32 characters in length.
			eg:
				vts delete parameter ParamBodyAngleX
				vts del ParamBodyAngleX
		*/
		match = Regex.Match(commandStr, @"^vts\s+del(?:ete)?(?:\s+parameter)?\s+([A-Za-z0-9]{4,32})$");
		if(match.Success)
		{
			string parameterName = match.Groups[1].Value;
			longCommandStr = $"vts delete parameter {parameterName}";
			shortCommandStr = $"vts del {parameterName}";
			parameters["parameterName"] = parameterName;
			parameters["type"] = MessageTypeEnum.ParameterDeletionRequest;
			return CommandTypeEnum.deleteparameter;
		}

		/*
			CommandTypeEnum.createcommand:
				You can trigger hotkeys either by their unique ID or the hotkey name (case-insensitive). 
			eg:
				vts create new command that press cl when hp<90 and area=九号解决方案
				vts press cl when hp<90 and area=九号解决方案
		*/
		match = Regex.Match(commandStr, @"^vts(?:\s+create\s+new\s+command\s+that)?\s+(press|start|stop)\s+(.+)\s+when\s+(.+)$");
		if(match.Success)
		{
			//校验命令(顺便创建payload)
			string type = match.Groups[1].Value;
			string command = match.Groups[2].Value;
			Dictionary<string, object> strAndPar = CheckCmd(type, command);
			string longCmdStr = (string)strAndPar["longCmdStr"];
			string shortCmdStr = (string)strAndPar["shortCmdStr"];
			String payload = new Message((Dictionary<string, object>)strAndPar["parameters"]).Serialize();

			//校验条件(顺便创建executer和judges)
			string condition = match.Groups[3].Value;//传入参数
			if(!CheckCondition(condition, payload, out string prepareConditionStr, out Judge[] judges))
			{
				Logger.Log($"CheckConditionException {prepareConditionStr}。");
			}
			
			longCommandStr = $"{longCmdStr} when {prepareConditionStr}";
			shortCommandStr = $"{shortCmdStr} when {prepareConditionStr}";
			parameters = (Dictionary<string, object>)strAndPar["parameters"];
			parameters["judges"] = judges;
			return CommandTypeEnum.createcommand;
		}

		/*
			CommandTypeEnum.deletecommand:
				删命令,包括定时器和指令,参数是0~999的数字.插件变量最少四位字符,所以不冲突
			eg:
				vts delete command 1
				vts del 1
				vts delete command 0,1,2,3,4,5,8,9,10,11
				vts del 0,1,2,3,4,5,8,9,10,11
				vts delete command all
				vts del all
		*/
		match = Regex.Match(commandStr, @"^vts\s+del(?:ete)?(?:\s+command)?\s+([A-Za-z0-9]{1,3}(?:,[A-Za-z0-9]{1,3})*)$");
		if(match.Success)
		{
			string commandIndex = match.Groups[1].Value;
			longCommandStr = $"vts delete command {commandIndex}";
			shortCommandStr = $"vts del {commandIndex}";
			parameters["commandIndex"] = commandIndex;
			return CommandTypeEnum.deletecommand;
		}

		/*
			CommandTypeEnum.showcommand:
				列出所有的命令,包括指令与定时器
			eg:
				vts show all commands
				vts show cmd
		*/
		match = Regex.Match(commandStr, @"^vts\s+show(\s+all)?\s+(commands|cmd)$");
		if(match.Success)
		{
			longCommandStr = "vts show all commands";
			shortCommandStr = "vts show cmd";
			return CommandTypeEnum.showcommand;
		}

		/*
			CommandTypeEnum.executecommand:
				直接执行指令,仅包括press/start/stop
			eg:
				vts execute command that press cl
				vts press cl
		*/
		match = Regex.Match(commandStr, @"^vts(?:\s+execute\s+command\s+that)?\s+(press|start|stop)\s+(.+)$");
		if(match.Success)
		{
			string type = match.Groups[1].Value;
			string command = match.Groups[2].Value;
			Dictionary<string, object> strAndPar = CheckCmd(type, command);
			longCommandStr = (string)strAndPar["longCmdStr"];
			shortCommandStr = (string)strAndPar["shortCmdStr"];
			parameters = (Dictionary<string, object>)strAndPar["parameters"];
			return CommandTypeEnum.executecommand;
		}

		/*
			CommandTypeEnum.schedular:
				给定时器的字典添加一个值,根据日志行更新,每秒发送一次
			eg:
				vts update schedular that set ParamBodyAngleX by hp
				vts set ParamBodyAngleX hp
				vts update schedular that set ParamBodyAngleX by %hp
				vts set ParamBodyAngleX %hp
		*/
		match = Regex.Match(commandStr, @"^vts(?:\s+update\s+schedular\s+that)?\s+set\s+([^\s]+)(?:\s+by)?\s+(?:(%)?(\w+))?$");
		if(match.Success)
		{
			string key = match.Groups[1].Value;//key
			string p = match.Groups[2].Value;//%
			string judgeKey = match.Groups[3].Value;//judgeKey
			parameters["key"] = key;
			parameters["p"] = p;
			parameters["judgeKey"] = judgeKey;
			longCommandStr = $"vts create schedular that set {key} by {p}{judgeKey}";
			shortCommandStr = $"vts set {key} {p}{judgeKey}";
			return CommandTypeEnum.schedular;
		}

		/*
			CommandTypeEnum.changeLog:
				改变日志级别.0为默认,负数为鲶鱼精邮差相关的输出方式
				数字的绝对值越大,打印的日志越多,详见Logger类
			eg:
				vts change log level 0
				vts log 0
				vts change log level 1
				vts log 1
				vts change log level -3
				vts log -3
		*/
		match = Regex.Match(commandStr, @"^vts(?:\s+change)?\s+log(?:\s+level)?\s+([\-\d]+)$");
		if(match.Success)
		{
			string level = match.Groups[1].Value;//日志级别
			parameters["level"] = level;
			longCommandStr = $"vts change log level {level}";
			shortCommandStr = $"vts log {level}";
			return CommandTypeEnum.changeLog;
		}

		longCommandStr = "vts did not match any usable commands";
		shortCommandStr = "vts did not match any usable commands";
		parameters = null;
		return CommandTypeEnum.donothing;
	}

	private static void VtsCreateCommand(string shortCommandStr, Dictionary<string, object> parameters)
	{
		//创建command对象
		Judge[] judges = (Judge[])parameters["judges"];
		parameters.Remove("judges");
		//存入持久化变量
		if(shortCommandStr != "")//初始化时也会调用这个方法,那时该参数为空字符串
		{
			int dvKey = _dv.Values.Count();
			while(_dv.ContainsKey(dvKey.ToString()))
			{
				dvKey++;
			}
			_dv.SetValue(dvKey.ToString(), shortCommandStr);
		}
	}

	private static void VtsDeleteCommand(Dictionary<string, object> parameters)
	{
		string commandIndexString = (string)parameters["commandIndex"];
		if(commandIndexString == "all") //删除全部
		{
			StaticHelpers.SetDictVariable(true, pluginName, new VariableDictionary());
		}
		else //删除部分
		{
			string[] commandIndexArray = commandIndexString.Split(',');
			foreach(string commandIndex in commandIndexArray)
			{
			    _dv.RemoveKey(commandIndex, pluginName);
			}
		}
		Init("acd");
	}

	private static void VtsShowCommand()
	{
		string[] stringKeys = _dv.Values.Keys.ToArray();
		int[] intKeys = new int[stringKeys.Length];
		for(int i = 0; i < stringKeys.Length; i++)
		{
			intKeys[i] = int.Parse(stringKeys[i]);
		}
		Array.Sort(intKeys);
		Dictionary<string, string> newDv = new Dictionary<string, string>();
		string allCommands = "";
		for (int i = 0; i < intKeys.Length; i++)
		{
			string key = i.ToString();
			string val = _dv.GetValue(intKeys[i].ToString()).ToString();
			newDv.Add(key, val);
			allCommands += $"{key}{val}\n";
		}
		if(intKeys.Length > 0)
		{
			Logger.Log($"have {intKeys.Length} command(s) and schedular:\n{allCommands.Substring(0, allCommands.Length - 1)}");
		}
		else
		{
			Logger.Log($"have neither command nor schedular");
		}
		_dv = new VariableDictionary(newDv);
		StaticHelpers.SetDictVariable(true, pluginName, _dv);
	}

	private static void VtsCreateSchedular(string shortCommandStr, Dictionary<string, object> parameters)
	{
		//先搓一个judge
		string key = (string)parameters["key"];
		string judgeKey = (string)parameters["judgeKey"];
		JudgeMethodEnum method = (string)parameters["p"] == "" ? JudgeMethodEnum.alltrue : JudgeMethodEnum.alltruep;
		Judge judge = new Judge(null, 0, judgeKey, method, key);
		//把judge放进去
		foreach(string handleNo in AuraCanDictionary.GetHandleNosByJudgeKey(judgeKey))
		{
			lock(_handles)
			{
				if(!_handles.ContainsKey(handleNo))
				{
					_handles[handleNo] = new Handle(handleNo);
				}
				lock(_handles[handleNo])
				{
					_handles[handleNo].AddJudge(judge);
				}
			}
		}
		//存入持久化变量
		if(shortCommandStr != "")//初始化时也会调用这个方法,那时该参数为空字符串
		{
			int dvKey = _dv.Values.Count();
			while(_dv.ContainsKey(dvKey.ToString()))
			{
				dvKey++;
			}
			_dv.SetValue(dvKey.ToString(), shortCommandStr);
		}
	}

	//true表示没问题,out格式化后的;false表示有问题,out异常提示
	private static bool CheckCondition(string conditionStr, string payload, out string prepareConditionStr, out Judge[] judges)
	{
		// todo 得检查下有没有这个参数
		// todo 还得查查各式是否合理
		prepareConditionStr = conditionStr;// todo 先直接放上了
		string[] parts = Regex.Split(conditionStr, @"\s+and\s+");
		int partsLength = parts.Length;
		//创建executer
		Executer executer = new Executer(partsLength, payload);
		//创建judges
		judges = new Judge[partsLength];
		for(int i=0; i<partsLength; i++)
		{
			Match match = Regex.Match(parts[i], @"^(\w+)([><=!]+)([\.\w]+)(%)?$");
			string judgeKey = match.Groups[1].Value;
			string sign = match.Groups[2].Value;
			JudgeMethodEnum method;
			object val;
			if(sign.Contains("="))
			{
				method = sign == "=" ? JudgeMethodEnum.eq : JudgeMethodEnum.ne;//=和!=
				val = match.Groups[3].Value;
			}
			else
			{
				if(!"".Equals(match.Groups[4]))//匹配到了%
				{
					method = match.Groups[2].Value == "<"? JudgeMethodEnum.ltp : JudgeMethodEnum.gtp;//<%和>%
				}
				else
				{
					method = match.Groups[2].Value == "<"? JudgeMethodEnum.lt : JudgeMethodEnum.gt;//<和>
				}
				val = double.Parse(match.Groups[3].Value);
			}
			if(judgeKey == "job" || judgeKey == "anyjob" )
			{
				val = AuraCanDictionary.GetJobIdByJobName((string)val);
			}
			Judge judge = new Judge(executer, i, judgeKey, method, val);
			foreach(string handleNo in AuraCanDictionary.GetHandleNosByJudgeKey(judgeKey))
			{
				lock(_handles)
				{
					if(!_handles.ContainsKey(handleNo))
					{
						_handles[handleNo] = new Handle(handleNo);
					}
					lock(_handles[handleNo])
					{
						_handles[handleNo].AddJudge(judge);
					}
				}
			}
			judges[i] = judge;
		}
		return true;
	}

	private static Dictionary<string, object> CheckCmd(string type, string command)
	{
		string longCmdStr = "";
		string shortCmdStr = "";
		Dictionary<string, object> strAndPar = new Dictionary<string, object>();
		Dictionary<string, object> parameters = new Dictionary<string, object>();
		if(type=="press") //热键
		{
			longCmdStr = $"vts create new command that press {command}";
			shortCmdStr = $"vts press {command}";
			parameters["hotkeyID"] = command;
			parameters["type"] = MessageTypeEnum.HotkeyTriggerRequest;
		}
		else if(type=="start") //表达式
		{
			longCmdStr = $"vts create new command that start {command}";
			shortCmdStr = $"vts start {command}";
			parameters["expressionFile"] = command + ".exp3.json";
			parameters["active"] = true;
			parameters["type"] = MessageTypeEnum.ExpressionActivationRequest;
		}
		else if(type=="stop") //表达式
		{
			longCmdStr = $"vts create new command that stop {command}";
			shortCmdStr = $"vts stop {command}";
			parameters["expressionFile"] = command + ".exp3.json";
			parameters["active"] = false;
			parameters["type"] = MessageTypeEnum.ExpressionActivationRequest;
		}
		strAndPar["longCmdStr"] = longCmdStr;
		strAndPar["shortCmdStr"] = shortCmdStr;
		strAndPar["parameters"] = parameters;
		return strAndPar;
	}
}

public static class AuraCanDictionary {
	private static Dictionary<string, string> _jobDic = new List<string>//职业字典
	{
		//逗号分割,第一个值为日志中的id,其余的值为可供输入的选项
		//防护职业
		"13,骑士","15,战士","20,暗黑骑士,黑骑","25,绝枪战士,绝枪,枪刃",
		"01,剑术师","03,斧术师",
		//治疗职业
		"18,白魔法师,白魔","1C,学者","21,占星术士,占星","28,贤者",
		"06,幻术师",
		//近战职业
		"14,武僧","16,龙骑士","1E,忍者","22,武士","27,钐镰客,镰刀","29,蝰蛇剑士",
		"02,格斗家","04,枪术师","1D,双剑师",
		//远程物理职业
		"17,吟游诗人,诗人","1F,机工士,机工","26,舞者",
		"05,弓箭手",
		//远程魔法职业
		"19,黑魔法师,黑魔","1B,召唤师,召唤","23,赤魔法师,赤魔","2A,绘灵法师,画家","24,青魔法师,青魔",
		"07,咒术师","1A,秘术师",
		//能工巧匠
		"08,刻木匠","09,锻铁匠","0A,铸甲匠","0B,雕金匠","0C,制革匠","0D,裁衣匠","0E,炼金术士","0F,烹调师",
		//大地使者
		"10,采矿工","11,园艺工","12,捕鱼人"
	}.Select(keyValueString => keyValueString.Split(','))
		.Select(parts => new { Value = parts[0], Keys = parts.Skip(1) })
		.SelectMany(x => x.Keys.Select(key => new { Value = x.Value, Key = key }))
		.ToDictionary(x => x.Key, x => x.Value);
	public static string GetJobIdByJobName(string jobName)
	{
		return _jobDic[jobName] == null ? jobName : _jobDic[jobName];
	}
	private static Dictionary<string, string> _commandDic = new Dictionary<string, string>//日志字段字典
	{
		//handleNo,id,judgeKey,judgeKey.ToUpper()
		//任意一人,id固定为空
		{ "anybuff", "26,,1;30,,1" },//"任意实体"被附加或移除状态
		{ "anybuffadd", "26,,1" },//"任意实体"被附加状态
		{ "anybuffremove", "30,,1" },//"任意实体"被移除状态
		{ "anyskill", "21,,3;22,,3" },//"任意实体"释放技能
		{ "anyjob", "03,,2" },//"任意实体"切换为某职业
		//自己,id为日志行中对应字段
		{ "area", "40,,2" },//如"九号解决方案"
		{ "areasub", "40,,3" },
		{ "buff", "26,5,1;30,5,1" },//"自己"被附加或移除状态
		{ "buffadd", "26,5,1" },//"自己"被附加状态
		{ "buffremove", "30,5,1" },//"自己"被移除状态
		{ "hp", "03,0,9,10;04,0,9,10;21,0,32,33;24,0,5,6;39,0,2,3" },
		{ "job", "03,0,2" },//"自己"切换为某职业
		{ "mp", "03,0,11,12;04,0,11,12;21,0,34,35;24,0,7,8;39,0,4,5" },
		{ "skill", "21,0,3;22,0,3" },//"自己"释放技能
		{ "target", "21,0,5" }//被自己使用了技能的目标
	};
	public static string[] GetHandleNosByJudgeKey(string judgeKey)
	{
		List<string> noList = new List<string>();
		string commandDicValue = _commandDic[judgeKey];
		foreach(string handlePart in commandDicValue.Split(';'))
		{
			string handleNo = handlePart.Split(',')[0];
			noList.Add(handleNo);
		}
		return noList.ToArray();
	}
	public static int GetIdIndexByHandleNo(string handleNo)
	{
		foreach(string commandInfo in _commandDic.Values)
		{
			string[] handles = commandInfo.Split(';');
			foreach(string handle in handles)
			{
				string[] pars = handle.Split(',');
				if(handleNo == pars[0])
				{
					return pars[1] == "" ? -1 : int.Parse(pars[1]);
				}
			}
		}
		return -1;//-1表示日志无实体字段
	}
	public static Dictionary<string, int> GetParsIndexByHandleNo(string handleNo)
	{
		Dictionary<string, int> parsIndex = new Dictionary<string, int>();
		foreach(KeyValuePair<string, string> kvp in _commandDic)
		{
			foreach(string handlePart in kvp.Value.Split(';'))
			{
				string[] logInfo = handlePart.Split(',');
				if(logInfo[0] == handleNo)
				{
					parsIndex.Add(kvp.Key, int.Parse(logInfo[2]));
					if(logInfo.Length > 3)
					{
						parsIndex.Add(kvp.Key.ToUpper(), int.Parse(logInfo[3]));
					}
					continue;
				}
			}
		}
		return parsIndex;
	}
}

public class Handle
{
	private List<Judge> _judges; //不同实体的判断列表
	private Dictionary<string, int> _parsIndex; //参数,位于日志中第几个
	private int _idIndex;//日志中第几个字段是实体的名字
	private string _handleNo;//日志行序号
	public Handle(string handleNo)
	{
		_judges = new List<Judge>();
		_parsIndex = AuraCanDictionary.GetParsIndexByHandleNo(handleNo);
		_idIndex = AuraCanDictionary.GetIdIndexByHandleNo(handleNo);
		_handleNo = handleNo;
	}
	public void AddJudge(Judge judge) => _judges.Add(judge);
	public void HandleProcess(string playerId, string[] log)
	{
		if(_idIndex != -1 && playerId != log[_idIndex])
		{
			return;
		}
		//有处理的必要才解析
		Dictionary<string, string> pars = new Dictionary<string, string>();
		foreach(KeyValuePair<string, int> kvp in _parsIndex)
		{
			pars.Add(kvp.Key,log[kvp.Value]);
		}
		string allAddPars = String.Join(",", pars.Select(kvp => $"{kvp.Key} {kvp.Value}"));
		Logger.Log2($"Handle{_handleNo} add par:{allAddPars}");
		for(int i = 0; i < _judges.Count; i++)
		{
			Logger.Log2($"handleNo:{_handleNo}'s judge, {i+1}/{_judges.Count}");
			_judges[i].JudgeProcess(pars);
		}
	}
}

public class Judge
{
	private Executer _executer;
	private int _index;//Executer数组中的索引
	private string _key;//比较参数在handle方法参数中的key
	private JudgeMethodEnum _method;
	private double _numVal;
	private string _strVal;
	public Judge(Executer executer, int index, string judgeKey, JudgeMethodEnum method, object val)
	{
		_executer = executer;
		_index = index;
		_key = judgeKey;
		_method = method;
		if (val is int || val is double)
		{
			_numVal = (double) val;
		}
		else
		{
			_strVal = (string) val;
		}
	}
	public void JudgeProcess(Dictionary<string, string> pars)
	{
		double percent;
		switch(_method)
		{
			case JudgeMethodEnum.gt:
				_executer.CheckSaveAndSend(_index, double.Parse(pars[_key]) > _numVal);
				break;
			case JudgeMethodEnum.lt:
				_executer.CheckSaveAndSend(_index, double.Parse(pars[_key]) < _numVal);
				break;
			case JudgeMethodEnum.eq:
				_executer.CheckSaveAndSend(_index, pars[_key] == _strVal);
				break;
			case JudgeMethodEnum.ne:
				_executer.CheckSaveAndSend(_index, pars[_key] != _strVal);
				break;
			case JudgeMethodEnum.gtp://>%
				percent = double.Parse(pars[_key]) * 100 / double.Parse(pars[_key.ToUpper()]);
				_executer.CheckSaveAndSend(_index, percent > _numVal);
				break;
			case JudgeMethodEnum.ltp://<%
				percent = double.Parse(pars[_key]) * 100 / double.Parse(pars[_key.ToUpper()]);
				_executer.CheckSaveAndSend(_index, percent < _numVal);
				break;
			case JudgeMethodEnum.alltrue://更新定时器用的
				Schedular.UpdateValue($"{_strVal}", double.Parse(pars[_key]));
				break;
			case JudgeMethodEnum.alltruep://更新定时器用的%(范围为0~1)
				Schedular.UpdateValue($"{_strVal}", double.Parse(pars[_key]) / double.Parse(pars[_key.ToUpper()]));
				break;
			default:
				throw new Exception($"钝口螈!有活力的钝口螈!");
				break;
		}
	}
}

public class Schedular //定时器
{
	private static System.Threading.Timer _timer;
	private static Dictionary<string, object> _setValDic;//key为自定义参数,value的格式为double
	private static int _count;//每秒一次日志太频繁了,加个计数器
	private static bool _sendFlag = false;//有额外发送则将值设为true,防止单周期发送大于2次
	public Schedular()
	{
		StaticHelpers.Storage.TryGetValue($"{pluginName}Timer", out object timerObj);
		_timer = (System.Threading.Timer)timerObj;
		if(_timer != null)
		{
			_timer.Dispose();
		}
		_timer = new System.Threading.Timer(SendSetMsg, null, 0, 900);
		StaticHelpers.Storage[$"{pluginName}Timer"] = _timer;
		_setValDic = new Dictionary<string, object>();
	}
	public static void StopSchedular() => _timer.Dispose();
	public static void UpdateValue(string key, double val)
	{
		_setValDic[key] = val;
		if(!_sendFlag)
		{
			_sendFlag = true;
			SendSetMsg(null);//缩短响应时间,有值修改立即发送一下
			Logger.Log2($"value {key} has changed, send immediately");
		}
	}
	private static void SendSetMsg(object _)
	{
		_sendFlag = false;
		if(_count-- == 0)
		{
			string allSetPars = String.Join(",", _setValDic.Select(pair => $"{pair.Key}"));
			Logger.Log2($"Schedular is working, param list:{allSetPars}");
			_count = 30;//30周期打印一次日志
		}
		if(_setValDic.Count == 0)
		{
			return;
		}
		Dictionary<string, object> parameters = new Dictionary<string, object>();
		parameters["mode"] = "set";
		parameters["type"] = MessageTypeEnum.InjectParameterDataRequest;
		List<Dictionary<string, object>> parameterValues = new List<Dictionary<string, object>>();
		foreach(KeyValuePair<string, object> kvp in _setValDic)
		{
			Dictionary<string, object> var = new Dictionary<string, object>();
			var["id"] = kvp.Key;
			var["value"] = kvp.Value;
			parameterValues.Add(var);
		}
		parameters["parameterValues"] = parameterValues;
		Message msg = new Message(parameters);
		AuraCanSocket.SendToVTubeStudio(msg.Serialize());
	}
}

public class Executer
{
	private bool[] _flags;
	private string _payload;
	public Executer(int length, string payload)
	{
		_flags = new bool[length];
		_payload = payload;
	}
	public void CheckSaveAndSend(int index, bool checkResult)
	{
		bool before = _flags.All(x => x);
		_flags[index] = checkResult;
		bool after = _flags.All(x => x);
		Logger.Log2($"before {before} after {after}, checkResult {checkResult}");
		if(!before&&after)
		{
			AuraCanSocket.SendToVTubeStudio(_payload);
		}
	}
}

public class Message {
	public string apiName { get; set; } = "VTubeStudioPublicAPI";
	public string apiVersion { get; set; } = "1.0";
	public string messageType { get; set; }
	public Dictionary<string, object> data { get; set; }
	public Message(Dictionary<string, object> data) {
		this.messageType = data["type"].ToString();
		data.Remove("type");
		this.data = data;
	}
	public string Serialize() => StaticHelpers.Serialize(this, false);
}


public static class AuraCanSocket {
	static WebSocket ws;
	public static void SendToVTubeStudio(string payload)
	{
		if (ws == null || ws.ReadyState != WebSocketState.Open) {
			ws = null;
			AuraCanVTS.DestoryHandles();//handles清空能解决大多数问题
			Logger.Log($"WebSocket 连接未开启,初始化_handles");
		}
		else
		{
			Logger.Log3("send:" + payload);
			ws.Send(payload);
		}
	}
	// 插件连接
	public static void InitSocket() {
		if (ws == null) {
			WebSocket _ws = new WebSocket(wsUrl);
			_ws.OnOpen += (_, __) => {
				ws = (WebSocket) _ws;
				Logger.Log2($"WebSocket OnOpen");
				Authenticate();
			};
			_ws.OnMessage += (_, e) => ProcessReceivedMessage(e.Data);
			_ws.OnClose += (_, __) => {
				ws = null;
				Logger.Log2($"WebSocket OnClose");
				Schedular.StopSchedular();
			};
			_ws.ConnectAsync();
		} else {
			Authenticate();
		}
	}
	private static void Authenticate() {
		string token = StaticHelpers.GetScalarVariable(true, $"{pluginName}Token") ?? "";
		MessageTypeEnum type;
		Dictionary<string, object> parameters = new Dictionary<string, object>();
		if (token == "") {
			type = MessageTypeEnum.AuthenticationTokenRequest;
			parameters["pluginIcon"] = "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";
		} else {
			type = MessageTypeEnum.AuthenticationRequest;
			parameters["authenticationToken"] = token;
		}
		parameters["pluginName"] = pluginName;
		parameters["pluginDeveloper"] = developer;
		parameters["type"] = type;
		string payload = new Message(parameters).Serialize();
		SendToVTubeStudio(payload);
	}
	private static void ProcessReceivedMessage(string json) {
		Logger.Log3($"receive:{json}");
		if (json.Contains("errorID")) {
			string errorId = GetValueFromJson(json, "errorID");
			if (errorId == "50" || errorId == "8") {
				StaticHelpers.SetScalarVariable(true, $"{pluginName}Token", "");
			}
			Logger.Log($"error:{json}");
		}
		if (json.Contains("authenticationToken")) {
			string token = GetValueFromJson(json, "authenticationToken");
			Logger.Log($"get and saved token");
			StaticHelpers.SetScalarVariable(true, $"{pluginName}Token", token);
			Authenticate();
		} else if (json.Contains("authenticated")) {
			if (GetValueFromJson(json, "authenticated") == "false") {
				Logger.Log($"lost token and try to re authenticate");
				StaticHelpers.SetScalarVariable(true, $"{pluginName}Token", "");
				Authenticate();
			}
		}
	}
	private static string GetValueFromJson(string jsonString, string key) {
		return new Regex("\"" + key + "\":\"?([^\",}]*)").Match(jsonString).Groups[1].Value;
	}
}

public enum CommandTypeEnum
{
	createparameter, //创建参数
	deleteparameter, //删除参数
	createcommand,//创建命令
	deletecommand, //删除命令
	showcommand, //列出已创建的命令
	executecommand, //立刻执行
	schedular, //调度器
	changeLog, //修改日志打印方式
	donothing //啥都不干(无匹配正则)
	//通过回调直接触发不使用枚举
}

public enum MessageTypeEnum {
	AuthenticationRequest, //身份验证1
	AuthenticationTokenRequest, //身份验证2
	ParameterCreationRequest, //添加新的自定义参数
	ParameterDeletionRequest, //删除自定义参数
	InjectParameterDataRequest, //输入默认或自定义参数的数据
	HotkeyTriggerRequest, //请求执行热键
	ExpressionActivationRequest //请求激活/停用表达式
}

public enum JudgeMethodEnum
{
	gt, //>
	lt, //<
	eq, //=
	ne, //!=
	gtp, //>%
	ltp, //<%
	alltrue, //一直为真,用于更新数据
	alltruep //一直为真,用于更新数据%
}

//日志打印
public static class Logger
{
	private static ILogger _logger;//重要信息(例如报错和关键提示信息)
	private static ILogger _logger2;//不太重要的信息(例如非关键提示信息)
	private static ILogger _logger3;//完全不重要的信息(发送的和接收的)
	public static void Log(string msg) => _logger.Log($"{pluginName} {msg}");
	public static void Log2(string msg) => _logger2.Log($"{pluginName} {msg}");
	public static void Log3(string msg) => _logger3.Log($"{pluginName} {msg}");
	private static PostNamazuLogger _postNamazuLogger = new PostNamazuLogger();//鲶鱼精
	private static TextAuraLogger _textAuraLogger = new TextAuraLogger();//文本悬浮窗
	private static TriggernometryLogger _triggernometryLogger = new TriggernometryLogger();//用户日志1
	private static TriggernometryLogger2 _triggernometryLogger2 = new TriggernometryLogger2();//用户日志2
	private static NoneLogger _noneLogger = new NoneLogger();//啥也不干
	private static readonly Dictionary<string, (ILogger logger, ILogger logger2, ILogger logger3)> loggers = new()
	{
		["-3"] = (_postNamazuLogger, _postNamazuLogger, _triggernometryLogger2), //鲶鱼精邮差+鲶鱼精邮差+用户日志2
		["-2"] = (_postNamazuLogger, _triggernometryLogger, _triggernometryLogger2), //鲶鱼精邮差+用户日志1+用户日志2
		["-1"] = (_postNamazuLogger, _triggernometryLogger, _noneLogger), //鲶鱼精邮差+用户日志1+啥也不干
		["0"] = (_noneLogger, _noneLogger, _noneLogger), //完全不打印日志
		["1"] = (_triggernometryLogger, _noneLogger, _noneLogger), //用户日志1+啥也不干+啥也不干
		["2"] = (_triggernometryLogger, _triggernometryLogger2, _noneLogger), //用户日志1+用户日志2+啥也不干
		["3"] = (_triggernometryLogger, _triggernometryLogger, _triggernometryLogger2), //用户日志1+用户日志1+用户日志2
		//文本悬浮窗有问题["4"] = (_textAuraLogger, _textAuraLogger, _textAuraLogger), //文本悬浮窗+文本悬浮窗+文本悬浮窗
	};
	public static void SetLogger(string level)//数字绝对值越大日志越多,负数区间分给鲶鱼精邮差
	{
		StaticHelpers.SetScalarVariable(true, $"{pluginName}LogLevel", level);
		if(loggers.TryGetValue(level, out var loggerPair))
		{
			_logger = loggerPair.logger;
			_logger2 = loggerPair.logger2;
			_logger3 = loggerPair.logger3;
		}
		else
		{
			Log($"日志级别{level}不存在,日志打印方式保持原样");
		}
	}
}
public interface ILogger
{
	void Log(string msg);
}
public class NoneLogger : ILogger//啥都不干
{
	public void Log(string msg) {}
}
public class TriggernometryLogger : ILogger//日志行(高级触发器用户日志)
{
	public void Log(string msg) => StaticHelpers.Log(RealPlugin.DebugLevelEnum.Custom, msg);
}
public class TriggernometryLogger2 : ILogger//日志行(高级触发器用户2日志)
{
	public void Log(string msg) => StaticHelpers.Log(RealPlugin.DebugLevelEnum.Custom2, msg);
}
public class TextAuraLogger : ILogger//文本悬浮窗(目前有问题)
{
	private static Triggernometry.Action _logAuraAction;
	public TextAuraLogger()
	{
		_logAuraAction = new Triggernometry.Action();
		_logAuraAction.ActionType = Triggernometry.Action.ActionTypeEnum.TextAura.ToString();
		_logAuraAction.AuraOp = Triggernometry.Action.AuraOpEnum.DeactivateAura.ToString();
		_logAuraAction.TextAuraName = pluginName;
		_logAuraAction.TextAuraAlignment = "TopLeft";
		_logAuraAction.TextAuraFontSize = "15";
		_logAuraAction.TextAuraXIniExpression = "0";
		_logAuraAction.TextAuraYIniExpression = "0";
		_logAuraAction.TextAuraWIniExpression = "1000";
		_logAuraAction.TextAuraHIniExpression = "1500";
		_logAuraAction.TextAuraOIniExpression = "100";
		_logAuraAction.TextAuraFontName = "Microsoft YaHei";
		_logAuraAction.TextAuraOutline = "#0080FF";
		_logAuraAction.TextAuraForeground = "White";
	}
	public void Log(string msg) {
		_logAuraAction.TextAuraExpression = msg;
		Context ctx = new Context();
		Triggernometry.RealPlugin.plug.QueueAction(ctx, ctx.trig, null, _logAuraAction, System.DateTime.Now, true);
	}
}
public class PostNamazuLogger : ILogger//鲶鱼精邮差
{
	public void Log(string msg) => Triggernometry.RealPlugin.plug.InvokeNamedCallback("command", "/e " + msg);
}