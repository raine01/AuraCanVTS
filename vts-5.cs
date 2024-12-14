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
AuraCanVTS.Init();

public static class AuraCanVTS {
	private static Schedular _schedular;//定时任务
	private static Dictionary<string, Handle> _handles;//日志行处理器列表
	private static Dictionary<int, Command> _commands;//指令列表
	private static VariableDictionary _dv;//持久变量
	private static string playerName;//当前玩家名称(好像没用?)

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
			Logger.Log2($"commandType: {commandType.ToString()}");
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
			Logger.Log($"command: {longCommandStr}");
		}
		else if(_handles.ContainsKey(handleNo))//其他日志处理(非默语)
		{
			//Log($"handleNo {handleNo}");
			logString = logString.Substring(37);
			string[] log = logString.Split('|');
			_handles[handleNo].Process(log);
		}
		if(handleNo == "02")//更新当前玩家的名称和ID(可能会有更换模型的动作,因此不作为elseif)
		{
			playerName = logString.Split('|')[3];
			Logger.Log("player " + playerName);
		}
	}

	public static void Init() => Init("abcde");
	public static void Init(string steps)
	{
		//初始化类
		if(steps.Contains("a"))
		{
			_handles = new Dictionary<string, Handle>();//key为10进制
			_commands = new Dictionary<int, Command>();//触发型命令列表
			string logLevel = StaticHelpers.GetScalarVariable(true, $"{pluginName}LogLevel") ?? "0";//日志级别,默认0
			Logger.SetLogger(logLevel);
			Logger.Log2($"init a finished");
		}
		//初始化websocket链接
		if(steps.Contains("b"))
		{
			AuraCanSocket.Init();
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
						Logger.Log2($"init schedular {commandStr}");
						CheckCommand(commandStr, out _, out _,out Dictionary<string, object> parameters);
						VtsCreateSchedular("", parameters);
					}
					else //start/stop/press
					{
						Logger.Log2($"init command {commandStr}");
						CheckCommand(commandStr, out _, out _,out Dictionary<string, object> parameters);
						VtsCreateCommand("", parameters);
					}
				}
				Logger.Log($"rebuild {_dv.Size} command(s)");
			}
			Logger.Log2($"init d finished");
		}
		//注册回调函数
		if(steps.Contains("e"))
		{
			RealPlugin.plug.RegisterNamedCallback(pluginName, new Action<object, string>(AuraCanVTS.LogProcess), null);
			Logger.Log2($"init e finished");
		}
		Logger.Log($"init finished");
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
				vts create new command that press cl when Raine'hp<90 and area=九号解决方案
				vts press cl when Raine'hp<90 and area=九号解决方案
		*/
		match = Regex.Match(commandStr, @"^vts(?:\s+create\s+new\s+command\s+that)?\s+(press|start|stop)\s+(.+)\s+when\s+(.+)$");
		if(match.Success)
		{
			//校验命令(顺便创建payload)
			Dictionary<string, string> typeAndCommand = new Dictionary<string, string>();
			typeAndCommand["type"] = match.Groups[1].Value;
			typeAndCommand["command"] = match.Groups[2].Value;
			if(!CheckCmd(typeAndCommand, out Dictionary<string, object> strAndPar))
			{
				string cmdErrMsg = (string)strAndPar["cmdErrMsg"];
				Logger.Log($"CheckCmdException {cmdErrMsg}。");
			}
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
				好像没啥要写的
			eg:
				vts delete command 1
				vts del 1
		*/
		match = Regex.Match(commandStr, @"^vts\s+del(?:ete)?(?:\s+command)?\s+([A-Za-z0-9]{1,3})$");
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
				好像也没啥要写的
			eg:
				vts show all commands
				vts show cmd
		*/
		match = Regex.Match(commandStr, @"^vts\s+show(\s+all)?\s+(commands|cmd)$");
		if(match.Success)
		{
			longCommandStr = "";
			shortCommandStr = "";
			return CommandTypeEnum.showcommand;
		}

		/*
			CommandTypeEnum.executecommand:
				好像同样没啥要写的
			eg:
				vts execute command that press cl
				vts press cl
				vts execute command that set FaceAngleX=10, FaceAngleY=50 weight 0.8, FaceAngleZ=-10
				vts set FaceAngleX=10 FaceAngleY=50 weight 0.8 FaceAngleZ=-10
		*/
		match = Regex.Match(commandStr, @"^vts(?:\s+execute\s+command\s+that)?\s+(press|start|stop)\s+(.+)$");
		if(match.Success)
		{
			Dictionary<string, string> typeAndCommand = new Dictionary<string, string>();//传入参数
			typeAndCommand["type"] = match.Groups[1].Value;
			typeAndCommand["command"] = match.Groups[2].Value;
			if(!CheckCmd(typeAndCommand, out Dictionary<string, object> strAndPar))//校验命令
			{
				string cmdErrMsg = (string)strAndPar["cmdErrMsg"];
				throw new Exception($"CheckCmdException {cmdErrMsg}。");
			}
			longCommandStr = (string)strAndPar["longCmdStr"];
			shortCommandStr = (string)strAndPar["shortCmdStr"];
			parameters = (Dictionary<string, object>)strAndPar["parameters"];
			return CommandTypeEnum.executecommand;
		}

		/*
			CommandTypeEnum.schedular:
				给定时器的字典添加一个值,根据日志行更新,每秒发送一次
			eg:
				vts update schedular that set ParamBodyAngleX by Raine's hp
				vts set ParamBodyAngleX Raine'hp
				vts update schedular that set ParamBodyAngleX by Raine's %hp
				vts set ParamBodyAngleX Raine'%hp
		*/
		match = Regex.Match(commandStr, @"^vts(?:\s+update\s+schedular\s+that)?\s+set\s+([^\s]+)(?:\s+by)?\s+(?:(?:([^\s]+)')?(?:s\s+)?(%)?(\w+))?$");
		if(match.Success)
		{
			string key = match.Groups[1].Value;//key
			string name = match.Groups[2].Value;//name
			string p = match.Groups[3].Value;//%
			string judgeKey = match.Groups[4].Value;//judgeKey
			parameters["key"] = key;
			parameters["name"] = name;
			parameters["p"] = p;
			parameters["judgeKey"] = judgeKey;
			longCommandStr = $"vts create schedular that set {key} by {name}'s {p}{judgeKey}";
			shortCommandStr = $"vts set {key} {name}'{p}{judgeKey}";
			return CommandTypeEnum.schedular;
		}

		/*
			CommandTypeEnum.changeLog:
				改变日志级别.0为默认,负数为鲶鱼精邮差相关的输出方式
				数字的绝对值越大,看的时候越容易感到头疼,详见Logger类
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

		longCommandStr = null;
		shortCommandStr = null;
		parameters = null;
		return CommandTypeEnum.donothing;
	}

	private static void VtsCreateCommand(string shortCommandStr, Dictionary<string, object> parameters)
	{
		//创建command对象
		Judge[] judges = (Judge[])parameters["judges"];
		parameters.Remove("judges");
		Command command = new Command(judges);
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
		//Log($"{_handles.ToString()} {_commands.ToString()} {_commandDic.ToString()}");
	}

	private static void VtsDeleteCommand(Dictionary<string, object> parameters)
	{
		//直接删除持久变量然后重新初始化
		string commandIndex = (string)parameters["commandIndex"];
		_dv.RemoveKey(commandIndex, pluginName);
		Init("acd");
	}

	private static void VtsShowCommand()
	{
		/*string allCommands = String.Join("\n", _dv.Values.Select(pair => $"{pair.Key}{pair.Value}"));
		Logger.Log(allCommands);*/
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
		_dv = new VariableDictionary(newDv);
	}

	private static void VtsCreateSchedular(string shortCommandStr, Dictionary<string, object> parameters)
	{
		//先搓一个judge
		string key = (string)parameters["key"];
		string name = (string)parameters["name"];
		string judgeKey = (string)parameters["judgeKey"];
		JudgeMethodEnum method = (string)parameters["p"] == "" ? JudgeMethodEnum.alltrue : JudgeMethodEnum.alltruep;
		Judge judge = new Judge(null, 0, judgeKey, method, key);//只有judgeKey和method有用
		//把judge放进去
		foreach(string handleNo in AuraCanDictionary.GetHandleNosByJudgeKey(judgeKey))
		{
			lock(_handles)
			{
				if(!_handles.ContainsKey(handleNo))
				{
					_handles[handleNo] = new Handle(handleNo);
				}
				_handles[handleNo].AddJudge(name, judge);
			}
		}
		//把参数放进Schedular
		Schedular.Add(key);
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
			Match match = Regex.Match(parts[i], @"^(?:(\w+)')?(\w+)([><=!]+)([\.\w]+)(%)?$");
			
			string name = match.Groups[1].Value;
			string judgeKey = match.Groups[2].Value;
			string sign = match.Groups[3].Value;
			JudgeMethodEnum method;
			object val;
			//AuraCanVTS.Log($"2.{judgeKey} {sign}");
			if(sign.Contains("="))
			{
				method = sign == "=" ? JudgeMethodEnum.eq : JudgeMethodEnum.ne;//=和!=
				val = match.Groups[4].Value;
			}
			else
			{
				if(!"".Equals(match.Groups[5]))//匹配到了%
				{
					method = match.Groups[3].Value == "<"? JudgeMethodEnum.ltp : JudgeMethodEnum.gtp;//<%和>%
				}
				else
				{
					method = match.Groups[3].Value == "<"? JudgeMethodEnum.lt : JudgeMethodEnum.gt;//<和>
				}
				val = double.Parse(match.Groups[4].Value);
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
					_handles[handleNo].AddJudge(name, judge);
				}
			}
			judges[i] = judge;
		}
		return true;
	}

	//true表示没问题,out格式化后的;false表示有问题,out异常提示
	private static bool CheckCmd(Dictionary<string, string> typeAndCommand, out Dictionary<string, object> strAndPar)
	{
		string longCmdStr = "";
		string shortCmdStr = "";
		string type = typeAndCommand["type"];
		string command = typeAndCommand["command"];
		strAndPar = new Dictionary<string, object>();
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
			shortCmdStr = $"vts start {command}";
			parameters["expressionFile"] = command + ".exp3.json";
			parameters["active"] = false;
			parameters["type"] = MessageTypeEnum.ExpressionActivationRequest;
		}
		strAndPar["longCmdStr"] = longCmdStr;
		strAndPar["shortCmdStr"] = shortCmdStr;
		strAndPar["parameters"] = parameters;
		return true;
	}
}

public static class AuraCanDictionary {
	private static Dictionary<string, string> _commandDic = new Dictionary<string, string>//日志字段字典
	{
		{ "hp", "24,1,5,6;39,1,2,3" },//handleNo,name,judgeKey,judgeKey.ToUpper()
		{ "area", "40,,2" },
		{ "areasub", "40,,3" }
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
	public static int GetNameIndexByHandleNo(string handleNo)
	{
		foreach(string commandInfo in _commandDic.Values)
		{
			string[] handles = commandInfo.Split(';');
			foreach(string handle in handles)
			{
				string[] pars = handle.Split(',');
				if(handleNo == pars[0])
				{
					return pars[1] == "" ? 0 : int.Parse(pars[1]);
				}
			}
		}
		return 0;//0表示日志无实体字段
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

public class Command
{
	private Judge[] _judges;
	public Command(Judge[] judges)
	{
		_judges = judges;
	}
	public Judge[] GetJudges()
	{
		return _judges;
	}
}

public class Handle
{
	private Dictionary<string, List<Judge>> _judges; //不同实体的判断列表
	private Dictionary<string, int> _parsIndex; //参数,位于日志中第几个
	private int _nameIndex;//日志中第几个字段是实体的名字
	private string _handleNo;//日志行序号
	public Handle(string handleNo)
	{
		_judges = new Dictionary<string, List<Judge>>();
		_parsIndex = AuraCanDictionary.GetParsIndexByHandleNo(handleNo);
		_nameIndex = AuraCanDictionary.GetNameIndexByHandleNo(handleNo);
		_handleNo = handleNo;
	}
	public void AddJudge(string name, Judge judge)
	{
		lock(_judges)
		{
			if(_judges.ContainsKey(name))
			{
				_judges[name].Add(judge);
			}
			else
			{
				List<Judge> judgeList = new List<Judge>();
				judgeList.Add(judge);
				_judges.Add(name, judgeList);
			}
		}
	}
	public void Process(string[] log)
	{
		bool theEntitieFlag = _nameIndex != 0 && _judges.ContainsKey(log[_nameIndex]);//存在特定实体判断列表
		bool anyEntitieFlag = _judges.ContainsKey("");//存在任意实体判断列表
		if(!theEntitieFlag && !anyEntitieFlag)
		{
			return;
		}
		//有处理的必要才解析
		Dictionary<string, string> pars = new Dictionary<string, string>();
		pars.Add("name", _nameIndex == 0 ? "" : log[_nameIndex]); //有名字就把名字塞进去
		foreach(KeyValuePair<string, int> kvp in _parsIndex)
		{
			pars.Add(kvp.Key,log[kvp.Value]);
			Logger.Log2($"parsAdd {_handleNo} {kvp.Key} {log[kvp.Value]}");
		}
		if(theEntitieFlag)//特定实体的判断列表
		{
			List<Judge> judges = _judges[log[_nameIndex]];
			for(int i = 0; i < judges.Count; i++)
			{
				Logger.Log2($"{_handleNo} judge {log[_nameIndex]} {i+1}/{judges.Count}");
				judges[i].Process(pars);
			}
		}
		if(anyEntitieFlag)//任意实体的判断列表
		{
			List<Judge> judges = _judges[""];
			for(int i = 0; i < judges.Count; i++)
			{
				Logger.Log2($"{_handleNo} judge all {i+1}/{judges.Count}");
				judges[i].Process(pars);
			}
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
	public void Process(Dictionary<string, string> pars)
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
	public Schedular()
	{
		if(_timer != null)
		{
			_timer.Dispose();
		}
		_setValDic = new Dictionary<string, object>();
		_timer = new System.Threading.Timer(SendSetMsg, null, 0, 500);
	}
	public static void Add(string key) => _setValDic[key] = "0";
	public static void UpdateValue(string key, double val) => _setValDic[key] = val;
	public static void DeleteValue(string key) => _setValDic.Remove(key);
	private static void SendSetMsg(object status)
	{
		if(_count-- == 0)
		{
			string allSetPars = String.Join(",", _setValDic.Select(pair => $"{pair.Key}"));
			Logger.Log2($"Schedular is working:{allSetPars}");
			_count = 60;
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
		Logger.Log($"before {before} after {after}, checkResult {checkResult}");
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
		if (ws.ReadyState != WebSocketState.Open) {
			ws = null;
			Logger.Log($"WebSocket 连接未开启。");
		}
		Logger.Log2("send:" + payload);
		ws.Send(payload);
	}
	// 插件连接
	public static void Init() {
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
			};
			_ws.ConnectAsync();
		} else {
			Authenticate();
		}
	}
	private static void Authenticate() {
		string token = StaticHelpers.GetScalarVariable(true, "ACtoken") ?? "";
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
		if (json.Contains("errorID")) {
			string errorId = GetValueFromJson(json, "errorID");
			if (errorId == "50" || errorId == "8") {
				StaticHelpers.SetScalarVariable(true, "ACtoken", "");
			}
			Logger.Log($"error:{json}");
		} else {
			Logger.Log2($"receive:{json}");
		}

		if (json.Contains("authenticationToken")) {
			string token = GetValueFromJson(json, "authenticationToken");
			Logger.Log($"get and saved token");
			StaticHelpers.SetScalarVariable(true, "ACtoken", token);
			Authenticate();
		} else if (json.Contains("authenticated")) {
			if (GetValueFromJson(json, "authenticated") == "false") {
				Logger.Log($"lost token and try to re authenticate");
				StaticHelpers.SetScalarVariable(true, "ACtoken", "");
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
	donothing //啥都不干
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
	private static ILogger _logger;//重要信息(报错,关键提示信息)
	private static ILogger _logger2;//不太重要的信息(非关键提示信息,json字符串)
	public static void Log(string msg) => _logger.Log(pluginName + "\n" + msg);
	public static void Log2(string msg) => _logger2.Log(pluginName + "\n" + msg);
	public static void SetLogger(string level)//数字绝对值越大日志越多,负数区间分给鲶鱼精邮差
	{
		StaticHelpers.SetScalarVariable(true, $"{pluginName}LogLevel", level);
		switch(level)
		{
			case "-3"://鲶鱼精+鲶鱼精
				_logger = new PostNamazuLogger();
				_logger2 = new PostNamazuLogger();
				break;
			case "-2"://鲶鱼精邮差+悬浮窗
				_logger = new PostNamazuLogger();
				_logger2 = new TriggernometryLogger();
				break;
			case "-1"://鲶鱼精邮差+日志行
				_logger = new PostNamazuLogger();
				_logger2 = new TriggernometryLogger();
				break;
			case "0"://仅日志行打印重要日志
				_logger = new TriggernometryLogger();
				_logger2 = new NoneLogger();
				break;
			case "1"://日志行打印全部日志
				_logger = new TriggernometryLogger();
				_logger2 = new TriggernometryLogger();
				break;
			case "2"://悬浮窗+日志行
				_logger = new TextAuraLogger();
				_logger2 = new TriggernometryLogger();
				break;
			case "3"://悬浮窗打印全部日志
				_logger = new TextAuraLogger();
				_logger2 = new TextAuraLogger();
				break;
			default:
				//回头再说
				break;
		}
	}
}
public interface ILogger
{
	void Log(string msg);
}
public class NoneLogger : ILogger//啥都不干
{
	public void Log(string msg)
	{
		//啥都不干
	}
}
public class TriggernometryLogger : ILogger//日志行(高级触发器日志)
{
	public void Log(string msg)
	{
		StaticHelpers.Log(RealPlugin.DebugLevelEnum.Custom, msg);
	}
}
public class TextAuraLogger : ILogger//悬浮窗
{
	public void Log(string msg)
	{
		//回头再写
	}
}
public class PostNamazuLogger : ILogger//鲶鱼精邮差
{
	public void Log(string msg)
	{
		Triggernometry.RealPlugin.plug.InvokeNamedCallback("command", "/e " + msg);
	}
}