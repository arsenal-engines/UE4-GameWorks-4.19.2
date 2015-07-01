﻿// Copyright 1998-2015 Epic Games, Inc. All Rights Reserved.
using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using AutomationTool;
using UnrealBuildTool;
using System.Reflection;
using System.Xml;
using System.Linq;

public partial class GUBP : BuildCommand
{
    public string StoreName = null;
	public string BranchName;
    public int CL = 0;
    public bool bSignBuildProducts = false;
    public bool bHasTests = false;
    public BranchInfo Branch = null;
    public bool bOrthogonalizeEditorPlatforms = false;
    public List<UnrealTargetPlatform> HostPlatforms;
    public bool bFake = false;
    public static bool bNoIOSOnPC = false;
    public static bool bForceIncrementalCompile = false;
    public string EmailHint;
    static public bool bPreflightBuild = false;
    public int PreflightShelveCL = 0;
    static public string PreflightMangleSuffix = "";
    public GUBPBranchHacker.BranchOptions BranchOptions = null;	

    Dictionary<string, NodeInfo> GUBPNodes;

	class NodeInfo
	{
		public GUBPNode Node;
	}

    class NodeHistory
    {
        public int LastSucceeded = 0;
        public int LastFailed = 0;
        public List<int> InProgress = new List<int>();
        public string InProgressString = "";
        public List<int> Failed = new List<int>();
        public string FailedString = "";
        public List<int> AllStarted = new List<int>();
        public List<int> AllSucceeded = new List<int>();
        public List<int> AllFailed = new List<int>();
    };

    public string AddNode(GUBPNode Node)
    {
        string Name = Node.GetFullName();
        if (GUBPNodes.ContainsKey(Name))
        {
            throw new AutomationException("Attempt to add a duplicate node {0}", Node.GetFullName());
        }
        GUBPNodes.Add(Name, new NodeInfo{ Node = Node });
        return Name;
    }

    public bool HasNode(string Node)
    {
        return GUBPNodes.ContainsKey(Node);
    }

    public GUBPNode FindNode(string Node)
    {
        return GUBPNodes[Node].Node;
    }

	public GUBPNode TryFindNode(string Node)
	{
		NodeInfo Result;
		GUBPNodes.TryGetValue(Node, out Result);
		return (Result == null)? null : Result.Node;
	}

    public void RemovePseudodependencyFromNode(string Node, string Dep)
    {
        if (!GUBPNodes.ContainsKey(Node))
        {
            throw new AutomationException("Node {0} not found", Node);
        }
        GUBPNodes[Node].Node.RemovePseudodependency(Dep);
    }

    public void RemoveAllPseudodependenciesFromNode(string Node)
    {
        if (!GUBPNodes.ContainsKey(Node))
        {
            throw new AutomationException("Node {0} not found", Node);
        }
        GUBPNodes[Node].Node.FullNamesOfPseudosependencies.Clear();
    }

    List<string> GetDependencies(string NodeToDo, bool bFlat = false, bool ECOnly = false)
    {
        List<string> Result = new List<string>();
        foreach (string Node in GUBPNodes[NodeToDo].Node.FullNamesOfDependencies)
        {
            bool Usable = GUBPNodes[Node].Node.RunInEC() || !ECOnly;
            if (Usable)
            {
                if (!Result.Contains(Node))
                {
                    Result.Add(Node);
                }
            }
            if (bFlat || !Usable)
            {
                foreach (string RNode in GetDependencies(Node, bFlat, ECOnly))
                {
                    if (!Result.Contains(RNode))
                    {
                        Result.Add(RNode);
                    }
                }
            }
        }
        foreach (string Node in GUBPNodes[NodeToDo].Node.FullNamesOfPseudosependencies)
        {
            bool Usable = GUBPNodes[Node].Node.RunInEC() || !ECOnly;
            if (Usable)
            {
                if (!Result.Contains(Node))
                {
                    Result.Add(Node);
                }
            }
            if (bFlat || !Usable)
            {
                foreach (string RNode in GetDependencies(Node, bFlat, ECOnly))
                {
                    if (!Result.Contains(RNode))
                    {
                        Result.Add(RNode);
                    }
                }
            }
        }
        return Result;
    }

	List<string> GetCompletedOnlyDependencies(string NodeToDo, bool bFlat = false, bool ECOnly = true)
	{
		List<string> Result = new List<string>();
		foreach (string Node in GUBPNodes[NodeToDo].Node.CompletedDependencies)
		{
			bool Usable = GUBPNodes[Node].Node.RunInEC() || !ECOnly;
			if(Usable)
			{
				if(!Result.Contains(Node))
				{
					Result.Add(Node);
				}
			}
			if (bFlat || !Usable)
			{
				foreach (string RNode in GetCompletedOnlyDependencies(Node, bFlat, ECOnly))
				{
					if (!Result.Contains(RNode))
					{
						Result.Add(RNode);
					}
				}
			}
		}
		return Result;
	}
    List<string> GetECDependencies(string NodeToDo, bool bFlat = false)
    {
        return GetDependencies(NodeToDo, bFlat, true);
    }
    bool NodeDependsOn(string Rootward, string Leafward)
    {
        List<string> Deps = GetDependencies(Leafward, true);
        return Deps.Contains(Rootward);
    }

    string GetControllingTrigger(string NodeToDo, Dictionary<string, string> GUBPNodesControllingTrigger)
    {
        if (GUBPNodesControllingTrigger.ContainsKey(NodeToDo))
        {
            return GUBPNodesControllingTrigger[NodeToDo];
        }
        string Result = "";
        foreach (string Node in GUBPNodes[NodeToDo].Node.FullNamesOfDependencies)
        {
            if (!GUBPNodes.ContainsKey(Node))
            {
                throw new AutomationException("Dependency {0} in {1} not found.", Node, NodeToDo);
            }
            bool IsTrigger = GUBPNodes[Node].Node.TriggerNode();
            if (IsTrigger)
            {
                if (Node != Result && !string.IsNullOrEmpty(Result))
                {
                    throw new AutomationException("Node {0} has two controlling triggers {1} and {2}.", NodeToDo, Node, Result);
                }
                Result = Node;
            }
            else
            {
                string NewResult = GetControllingTrigger(Node, GUBPNodesControllingTrigger);
                if (!String.IsNullOrEmpty(NewResult))
                {
                    if (NewResult != Result && !string.IsNullOrEmpty(Result))
                    {
                        throw new AutomationException("Node {0} has two controlling triggers {1} and {2}.", NodeToDo, NewResult, Result);
                    }
                    Result = NewResult;
                }
            }
        }
        foreach (string Node in GUBPNodes[NodeToDo].Node.FullNamesOfPseudosependencies)
        {
            if (!GUBPNodes.ContainsKey(Node))
            {
                throw new AutomationException("Pseudodependency {0} in {1} not found.", Node, NodeToDo);
            }
            bool IsTrigger = GUBPNodes[Node].Node.TriggerNode();
            if (IsTrigger)
            {
                if (Node != Result && !string.IsNullOrEmpty(Result))
                {
                    throw new AutomationException("Node {0} has two controlling triggers {1} and {2}.", NodeToDo, Node, Result);
                }
                Result = Node;
            }
            else
            {
                string NewResult = GetControllingTrigger(Node, GUBPNodesControllingTrigger);
                if (!String.IsNullOrEmpty(NewResult))
                {
                    if (NewResult != Result && !string.IsNullOrEmpty(Result))
                    {
                        throw new AutomationException("Node {0} has two controlling triggers {1} and {2}.", NodeToDo, NewResult, Result);
                    }
                    Result = NewResult;
                }
            }
        }
        GUBPNodesControllingTrigger.Add(NodeToDo, Result);
        return Result;
    }
    string GetControllingTriggerDotName(string NodeToDo, Dictionary<string, string> GUBPNodesControllingTriggerDotName, Dictionary<string, string> GUBPNodesControllingTrigger)
    {
        if (GUBPNodesControllingTriggerDotName.ContainsKey(NodeToDo))
        {
            return GUBPNodesControllingTriggerDotName[NodeToDo];
        }
        string Result = "";
        string WorkingNode = NodeToDo;
        while (true)
        {
            string ThisResult = GetControllingTrigger(WorkingNode, GUBPNodesControllingTrigger);
            if (ThisResult == "")
            {
                break;
            }
            if (Result != "")
            {
                Result = "." + Result;
            }
            Result = ThisResult + Result;
            WorkingNode = ThisResult;
        }
        GUBPNodesControllingTriggerDotName.Add(NodeToDo, Result);
        return Result;
    }

    public string CISFrequencyQuantumShiftString(string NodeToDo)
    {
        string FrequencyString = "";
        int Quantum = GUBPNodes[NodeToDo].Node.DependentCISFrequencyQuantumShift();
        if (Quantum > 0)
        {
			int TimeQuantum = 20;
			if(BranchOptions.QuantumOverride != 0)
			{
				TimeQuantum = BranchOptions.QuantumOverride;
			}
            int Minutes = TimeQuantum * (1 << Quantum);
            if (Minutes < 60)
            {
                FrequencyString = string.Format(" ({0}m)", Minutes);
            }
            else
            {
                FrequencyString = string.Format(" ({0}h{1}m)", Minutes / 60, Minutes % 60);
            }
        }
        return FrequencyString;
    }

    public int ComputeDependentCISFrequencyQuantumShift(string NodeToDo, Dictionary<string, int> FrequencyOverrides)
    {
        int Result = GUBPNodes[NodeToDo].Node.ComputedDependentCISFrequencyQuantumShift;        
        if (Result < 0)
        {
            Result = GUBPNodes[NodeToDo].Node.CISFrequencyQuantumShift(this);
            Result = GetFrequencyForNode(this, GUBPNodes[NodeToDo].Node.GetFullName(), Result);

			int FrequencyOverride;
			if(FrequencyOverrides.TryGetValue(NodeToDo, out FrequencyOverride) && Result > FrequencyOverride)
			{
				Result = FrequencyOverride;
			}

            foreach (string Dep in GUBPNodes[NodeToDo].Node.FullNamesOfDependencies)
            {
                Result = Math.Max(ComputeDependentCISFrequencyQuantumShift(Dep, FrequencyOverrides), Result);
            }
            foreach (string Dep in GUBPNodes[NodeToDo].Node.FullNamesOfPseudosependencies)
            {
                Result = Math.Max(ComputeDependentCISFrequencyQuantumShift(Dep, FrequencyOverrides), Result);
            }
			foreach (string Dep in GUBPNodes[NodeToDo].Node.CompletedDependencies)
			{
				Result = Math.Max(ComputeDependentCISFrequencyQuantumShift(Dep, FrequencyOverrides), Result);
			}
            if (Result < 0)
            {
                throw new AutomationException("Failed to compute shift.");
            }
            GUBPNodes[NodeToDo].Node.ComputedDependentCISFrequencyQuantumShift = Result;
        }
        return Result;
    }

    bool NodeIsAlreadyComplete(Dictionary<string, bool> GUBPNodesCompleted, string NodeToDo, bool LocalOnly)
    {
        if (GUBPNodesCompleted.ContainsKey(NodeToDo))
        {
            return GUBPNodesCompleted[NodeToDo];
        }
        string NodeStoreName = StoreName + "-" + GUBPNodes[NodeToDo].Node.GetFullName();
        string GameNameIfAny = GUBPNodes[NodeToDo].Node.GameNameIfAnyForTempStorage();
        bool Result;
        if (LocalOnly)
        {
            Result = TempStorage.LocalTempStorageExists(CmdEnv, NodeStoreName, bQuiet : true);
        }
        else
        {
            Result = TempStorage.TempStorageExists(CmdEnv, NodeStoreName, GameNameIfAny, bQuiet: true);
			if(GameNameIfAny != "" && Result == false)
			{
				Result = TempStorage.TempStorageExists(CmdEnv, NodeStoreName, "", bQuiet: true);
			}
        }
        if (Result)
        {
            LogVerbose("***** GUBP Trigger Node was already triggered {0} -> {1} : {2}", GUBPNodes[NodeToDo].Node.GetFullName(), GameNameIfAny, NodeStoreName);
        }
        else
        {
            LogVerbose("***** GUBP Trigger Node was NOT yet triggered {0} -> {1} : {2}", GUBPNodes[NodeToDo].Node.GetFullName(), GameNameIfAny, NodeStoreName);
        }
        GUBPNodesCompleted.Add(NodeToDo, Result);
        return Result;
    }
    string RunECTool(string Args, bool bQuiet = false)
    {
        if (ParseParam("FakeEC"))
        {
            LogWarning("***** Would have ran ectool {0}", Args);
            return "We didn't actually run ectool";
        }
        else
        {
            ERunOptions Opts = ERunOptions.Default;
            if (bQuiet)
            {
                Opts = (Opts & ~ERunOptions.AllowSpew) | ERunOptions.NoLoggingOfRunCommand;
            }
            return RunAndLog("ectool", "--timeout 900 " + Args, Options: Opts);
        }
    }
    void WriteECPerl(List<string> Args)
    {
        Args.Add("$batch->submit();");
        string ECPerlFile = CommandUtils.CombinePaths(CommandUtils.CmdEnv.LogFolder, "jobsteps.pl");
        WriteAllLines_NoExceptions(ECPerlFile, Args.ToArray());
    }
    string GetEMailListForNode(GUBP bp, string NodeToDo, string Emails, string Causers)
    {        
        string BranchForEmail = "";
        if (P4Enabled)
        {
            BranchForEmail = P4Env.BuildRootP4;
        }
        return HackEmails(Emails, Causers, BranchForEmail, NodeToDo);
    }
    int GetFrequencyForNode(GUBP bp, string NodeToDo, int BaseFrequency)
    {
        return HackFrequency(bp, BranchName, NodeToDo, BaseFrequency);
    }

    List<P4Connection.ChangeRecord> GetChanges(int LastOutputForChanges, int TopCL, int LastGreen)
    {
        List<P4Connection.ChangeRecord> Result = new List<P4Connection.ChangeRecord>();
        if (TopCL > LastGreen)
        {
            if (LastOutputForChanges > 1990000)
            {
                string Cmd = String.Format("{0}@{1},{2} {3}@{4},{5}",
                    CombinePaths(PathSeparator.Slash, P4Env.BuildRootP4, "...", "Source", "..."), LastOutputForChanges + 1, TopCL,
                    CombinePaths(PathSeparator.Slash, P4Env.BuildRootP4, "...", "Build", "..."), LastOutputForChanges + 1, TopCL
                    );
                List<P4Connection.ChangeRecord> ChangeRecords;
				if (P4.Changes(out ChangeRecords, Cmd, false, true, LongComment: true))
                {
                    foreach (P4Connection.ChangeRecord Record in ChangeRecords)
                    {
                        if (!Record.User.Equals("buildmachine", StringComparison.InvariantCultureIgnoreCase))
                        {
                            Result.Add(Record);
                        }
                    }
                }
                else
                {
                    throw new AutomationException("Could not get changes; cmdline: p4 changes {0}", Cmd);
                }
            }
            else
            {
                throw new AutomationException("That CL looks pretty far off {0}", LastOutputForChanges);
            }
        }
        return Result;
    }

    int PrintChanges(int LastOutputForChanges, int TopCL, int LastGreen)
    {
        List<P4Connection.ChangeRecord> ChangeRecords = GetChanges(LastOutputForChanges, TopCL, LastGreen);
        foreach (P4Connection.ChangeRecord Record in ChangeRecords)
        {
            string Summary = Record.Summary.Replace("\r", "\n");
            if (Summary.IndexOf("\n") > 0)
            {
                Summary = Summary.Substring(0, Summary.IndexOf("\n"));
            }
            Log("         {0} {1} {2}", Record.CL, Record.UserEmail, Summary);
        }
        return TopCL;
    }

    void PrintDetailedChanges(NodeHistory History, bool bShowAllChanges = false)
    {
        DateTime StartTime = DateTime.UtcNow;

        string Me = String.Format("{0}   <<<< local sync", P4Env.Changelist);
        int LastOutputForChanges = 0;
        int LastGreen = History.LastSucceeded;
        if (bShowAllChanges)
        {
            if (History.AllStarted.Count > 0)
            {
                LastGreen = History.AllStarted[0];
            }
        }
        foreach (int cl in History.AllStarted)
        {
            if (cl < LastGreen)
            {
                continue;
            }
            if (P4Env.Changelist < cl && Me != "")
            {
                LastOutputForChanges = PrintChanges(LastOutputForChanges, P4Env.Changelist, LastGreen);
                Log("         {0}", Me);
                Me = "";
            }
            string Status = "In Process";
            if (History.AllSucceeded.Contains(cl))
            {
                Status = "ok";
            }
            if (History.AllFailed.Contains(cl))
            {
                Status = "FAIL";
            }
            LastOutputForChanges = PrintChanges(LastOutputForChanges, cl, LastGreen);
            Log("         {0}   {1}", cl, Status);
        }
        if (Me != "")
        {
            LastOutputForChanges = PrintChanges(LastOutputForChanges, P4Env.Changelist, LastGreen);
            Log("         {0}", Me);
        }
        double BuildDuration = (DateTime.UtcNow - StartTime).TotalMilliseconds;
        Log("Took {0}s to get P4 history", BuildDuration / 1000);

    }
    void PrintNodes(GUBP bp, List<string> Nodes, Dictionary<string, bool> GUBPNodesCompleted, Dictionary<string, NodeHistory> GUBPNodesHistory, Dictionary<string, string> GUBPNodesControllingTriggerDotName, Dictionary<string, string> GUBPNodesControllingTrigger, bool LocalOnly, List<string> UnfinishedTriggers = null)
    {
        bool bShowAllChanges = bp.ParseParam("AllChanges") && GUBPNodesHistory != null;
        bool bShowChanges = (bp.ParseParam("Changes") && GUBPNodesHistory != null) || bShowAllChanges;
        bool bShowDetailedHistory = (bp.ParseParam("History") && GUBPNodesHistory != null) || bShowChanges;
        bool bShowDependencies = bp.ParseParam("ShowDependencies");
		bool bShowDependednOn = bp.ParseParam("ShowDependedOn");
		bool bShowDependentPromotions = bp.ParseParam("ShowDependentPromotions");
        bool bShowECDependencies = bp.ParseParam("ShowECDependencies");
        bool bShowHistory = !bp.ParseParam("NoHistory") && GUBPNodesHistory != null;
        bool AddEmailProps = bp.ParseParam("ShowEmails");
        bool ECProc = bp.ParseParam("ShowECProc");
        bool ECOnly = bp.ParseParam("ShowECOnly");
        bool bShowTriggers = true;
        string LastControllingTrigger = "";
        string LastAgentGroup = "";
        foreach (string NodeToDo in Nodes)
        {
            if (ECOnly && !GUBPNodes[NodeToDo].Node.RunInEC())
            {
                continue;
            }
            string EMails = "";
            if (AddEmailProps)
            {
                EMails = GetEMailListForNode(bp, NodeToDo, "", "");
            }
            if (bShowTriggers)
            {
                string MyControllingTrigger = GetControllingTriggerDotName(NodeToDo, GUBPNodesControllingTriggerDotName, GUBPNodesControllingTrigger);
                if (MyControllingTrigger != LastControllingTrigger)
                {
                    LastControllingTrigger = MyControllingTrigger;
                    if (MyControllingTrigger != "")
                    {
                        string Finished = "";
                        if (UnfinishedTriggers != null)
                        {
                            string MyShortControllingTrigger = GetControllingTrigger(NodeToDo, GUBPNodesControllingTrigger);
                            if (UnfinishedTriggers.Contains(MyShortControllingTrigger))
                            {
                                Finished = "(not yet triggered)";
                            }
                            else
                            {
                                Finished = "(already triggered)";
                            }
                        }
                        Log("  Controlling Trigger: {0}    {1}", MyControllingTrigger, Finished);
                    }
                }
            }
            if (GUBPNodes[NodeToDo].Node.AgentSharingGroup != LastAgentGroup && GUBPNodes[NodeToDo].Node.AgentSharingGroup != "")
            {
                Log("    Agent Group: {0}", GUBPNodes[NodeToDo].Node.AgentSharingGroup);
            }
            LastAgentGroup = GUBPNodes[NodeToDo].Node.AgentSharingGroup;

            string Agent = GUBPNodes[NodeToDo].Node.ECAgentString();
			if(ParseParamValue("AgentOverride") != "" && !GUBPNodes[NodeToDo].Node.GetFullName().Contains("Mac"))
			{
				Agent = ParseParamValue("AgentOverride");
			}
            if (Agent != "")
            {
                Agent = "[" + Agent + "]";
            }
            string MemoryReq = "[" + GUBPNodes[NodeToDo].Node.AgentMemoryRequirement(bp).ToString() + "]";            
            if(MemoryReq == "[0]")
            {
                MemoryReq = "";                
            }
            string FrequencyString = CISFrequencyQuantumShiftString(NodeToDo);

            Log("      {0}{1}{2}{3}{4}{5}{6} {7}  {8}",
                (LastAgentGroup != "" ? "  " : ""),
                NodeToDo,
                FrequencyString,
                NodeIsAlreadyComplete(GUBPNodesCompleted, NodeToDo, LocalOnly) ? " - (Completed)" : "",
                GUBPNodes[NodeToDo].Node.TriggerNode() ? " - (TriggerNode)" : "",
                GUBPNodes[NodeToDo].Node.IsSticky() ? " - (Sticky)" : "",				
                Agent,
                MemoryReq,
                EMails,
                ECProc ? GUBPNodes[NodeToDo].Node.ECProcedure() : ""
                );
            if (bShowHistory && GUBPNodesHistory.ContainsKey(NodeToDo))
            {
                NodeHistory History = GUBPNodesHistory[NodeToDo];

                if (bShowDetailedHistory)
                {
                    PrintDetailedChanges(History, bShowAllChanges);
                }
                else
                {
                    Log("         Last Success: {0}", History.LastSucceeded);
                    Log("         Last Fail   : {0}", History.LastFailed);
                    Log("          Fails Since: {0}", History.FailedString);
                    Log("     InProgress Since: {0}", History.InProgressString);					
                }
            }
            if (bShowDependencies)
            {
                foreach (string Dep in GUBPNodes[NodeToDo].Node.FullNamesOfDependencies)
                {
                    Log("            dep> {0}", Dep);
                }
                foreach (string Dep in GUBPNodes[NodeToDo].Node.FullNamesOfPseudosependencies)
                {
                    Log("           pdep> {0}", Dep);
                }
				foreach (string Dep in GUBPNodes[NodeToDo].Node.CompletedDependencies)
				{
					Log("			cdep>{0}", Dep);
				}
            }
            if (bShowECDependencies)
            {
                foreach (string Dep in GetECDependencies(NodeToDo))
                {
                    Log("           {0}", Dep);
                }
				foreach (string Dep in GetCompletedOnlyDependencies(NodeToDo))
				{
					Log("           compDep> {0}", Dep);
				}
            }

			if(bShowDependednOn)
			{
				foreach (string Dep in GUBPNodes[NodeToDo].Node.FullNamesOfDependedOn)
				{
					Log("            depOn> {0}", Dep);
				}
			}
			if (bShowDependentPromotions)
			{
				foreach (string Dep in GUBPNodes[NodeToDo].Node.DependentPromotions)
				{
					Log("            depPro> {0}", Dep);
				}
			}			
        }
    }
    public void SaveGraphVisualization(List<string> Nodes)
    {
        List<GraphNode> GraphNodes = new List<GraphNode>();

        Dictionary<string, GraphNode> NodeToGraphNodeMap = new Dictionary<string, GraphNode>();

        for (int NodeIndex = 0; NodeIndex < Nodes.Count; ++NodeIndex)
        {
            string Node = Nodes[NodeIndex];

            GraphNode GraphNode = new GraphNode()
            {
                Id = GraphNodes.Count,
                Label = Node
            };
            GraphNodes.Add(GraphNode);
            NodeToGraphNodeMap.Add(Node, GraphNode);
        }

        // Connect everything together
        List<GraphEdge> GraphEdges = new List<GraphEdge>();

        for (int NodeIndex = 0; NodeIndex < Nodes.Count; ++NodeIndex)
        {
            string Node = Nodes[NodeIndex];
            GraphNode NodeGraphNode = NodeToGraphNodeMap[Node];

            foreach (string Dep in GUBPNodes[Node].Node.FullNamesOfDependencies)
            {
                GraphNode PrerequisiteFileGraphNode;
                if (NodeToGraphNodeMap.TryGetValue(Dep, out PrerequisiteFileGraphNode))
                {
                    // Connect a file our action is dependent on, to our action itself
                    GraphEdge NewGraphEdge = new GraphEdge()
                    {
                        Id = GraphEdges.Count,
                        Source = PrerequisiteFileGraphNode,
                        Target = NodeGraphNode,
                        Color = new GraphColor() { R = 0.0f, G = 0.0f, B = 0.0f, A = 0.75f }
                    };

                    GraphEdges.Add(NewGraphEdge);
                }

            }
            foreach (string Dep in GUBPNodes[Node].Node.FullNamesOfPseudosependencies)
            {
                GraphNode PrerequisiteFileGraphNode;
                if (NodeToGraphNodeMap.TryGetValue(Dep, out PrerequisiteFileGraphNode))
                {
                    // Connect a file our action is dependent on, to our action itself
                    GraphEdge NewGraphEdge = new GraphEdge()
                    {
                        Id = GraphEdges.Count,
                        Source = PrerequisiteFileGraphNode,
                        Target = NodeGraphNode,
                        Color = new GraphColor() { R = 0.0f, G = 0.0f, B = 0.0f, A = 0.25f }
                    };

                    GraphEdges.Add(NewGraphEdge);
                }

            }
        }

        string Filename = CommandUtils.CombinePaths(CommandUtils.CmdEnv.LogFolder, "GubpGraph.gexf");
        Log("Writing graph to {0}", Filename);
        GraphVisualization.WriteGraphFile(Filename, "GUBP Nodes", GraphNodes, GraphEdges);
        Log("Wrote graph to {0}", Filename);
     }


    // when the host is win64, this is win32 because those are also "host platforms"
    static public UnrealTargetPlatform GetAltHostPlatform(UnrealTargetPlatform HostPlatform)
    {
        UnrealTargetPlatform AltHostPlatform = UnrealTargetPlatform.Unknown; // when the host is win64, this is win32 because those are also "host platforms"
        if (HostPlatform == UnrealTargetPlatform.Win64)
        {
            AltHostPlatform = UnrealTargetPlatform.Win32;
        }
        return AltHostPlatform;
    }

    List<int> ConvertCLToIntList(List<string> Strings)
    {
        List<int> Result = new List<int>();
        foreach (string ThisString in Strings)
        {
            int ThisInt = int.Parse(ThisString);
            if (ThisInt < 1960000 || ThisInt > 3000000)
            {
                Log("CL {0} appears to be out of range", ThisInt);
            }
            Result.Add(ThisInt);
        }
        Result.Sort();
        return Result;
    }
    void SaveStatus(string NodeToDo, string Suffix, string NodeStoreName, bool bSaveSharedTempStorage, string GameNameIfAny, string JobStepIDForFailure = null)
    {
        string Contents = "Just a status record: " + Suffix;
        if (!String.IsNullOrEmpty(JobStepIDForFailure) && IsBuildMachine)
        {
            try
            {
                Contents = RunECTool(String.Format("getProperties --jobStepId {0} --recurse 1", JobStepIDForFailure), true);
            }
            catch (Exception Ex)
            {
                Log(System.Diagnostics.TraceEventType.Warning, "Failed to get properties for jobstep to save them.");
                Log(System.Diagnostics.TraceEventType.Warning, LogUtils.FormatException(Ex));
            }
        }
        string RecordOfSuccess = CombinePaths(CommandUtils.CmdEnv.LocalRoot, "Engine", "Saved", "Logs", NodeToDo + Suffix +".log");
        CreateDirectory(Path.GetDirectoryName(RecordOfSuccess));
        WriteAllText(RecordOfSuccess, Contents);		
		TempStorage.StoreToTempStorage(CmdEnv, NodeStoreName + Suffix, new List<string> { RecordOfSuccess }, !bSaveSharedTempStorage, GameNameIfAny);		
    }
	string GetPropertyFromStep(string PropertyPath)
	{
		string Property = "";
		Property = RunECTool("getProperty \"" + PropertyPath + "\"");
		Property = Property.TrimEnd('\r', '\n');
		return Property;
	}

    int CountZeros(int Num)
    {
        if (Num < 0)
        {
            throw new AutomationException("Bad CountZeros");
        }
        if (Num == 0)
        {
            return 31;
        }
        int Result = 0;
        while ((Num & 1) == 0)
        {
            Result++;
            Num >>= 1;
        }
        return Result;
    }

    List<string> TopologicalSort(HashSet<string> NodesToDo, Dictionary<string, bool> GUBPNodesCompleted, Dictionary<string, string> GUBPNodesControllingTriggerDotName, Dictionary<string, string> GUBPNodesControllingTrigger, string ExplicitTrigger = "", bool LocalOnly = false, bool SubSort = false, bool DoNotConsiderCompletion = false)
    {
        DateTime StartTime = DateTime.UtcNow;

        List<string> OrdereredToDo = new List<string>();

        Dictionary<string, List<string>> SortedAgentGroupChains = new Dictionary<string, List<string>>();
        if (!SubSort)
        {
            Dictionary<string, List<string>> AgentGroupChains = new Dictionary<string, List<string>>();
            foreach (string NodeToDo in NodesToDo)
            {
                string MyAgentGroup = GUBPNodes[NodeToDo].Node.AgentSharingGroup;
                if (MyAgentGroup != "")
                {
                    if (!AgentGroupChains.ContainsKey(MyAgentGroup))
                    {
                        AgentGroupChains.Add(MyAgentGroup, new List<string> { NodeToDo });
                    }
                    else
                    {
                        AgentGroupChains[MyAgentGroup].Add(NodeToDo);
                    }
                }
            }
            foreach (KeyValuePair<string, List<string>> Chain in AgentGroupChains)
            {
                SortedAgentGroupChains.Add(Chain.Key, TopologicalSort(new HashSet<string>(Chain.Value), GUBPNodesCompleted, GUBPNodesControllingTriggerDotName, GUBPNodesControllingTrigger, ExplicitTrigger, LocalOnly, true, DoNotConsiderCompletion));
            }
            Log("***************Done with recursion");
        }

        // here we do a topological sort of the nodes, subject to a lexographical and priority sort
        while (NodesToDo.Count > 0)
        {
            bool bProgressMade = false;
            float BestPriority = -1E20f;
            string BestNode = "";
            bool BestPseudoReady = false;
            HashSet<string> NonReadyAgentGroups = new HashSet<string>();
            HashSet<string> NonPeudoReadyAgentGroups = new HashSet<string>();
            HashSet<string> ExaminedAgentGroups = new HashSet<string>();
            foreach (string NodeToDo in NodesToDo)
            {
                bool bReady = true;
				bool bPseudoReady = true;
				bool bCompReady = true;
                if (!SubSort && GUBPNodes[NodeToDo].Node.AgentSharingGroup != "")
                {
                    if (ExaminedAgentGroups.Contains(GUBPNodes[NodeToDo].Node.AgentSharingGroup))
                    {
                        bReady = !NonReadyAgentGroups.Contains(GUBPNodes[NodeToDo].Node.AgentSharingGroup);
                        bPseudoReady = !NonPeudoReadyAgentGroups.Contains(GUBPNodes[NodeToDo].Node.AgentSharingGroup); //this might not be accurate if bReady==false
                    }
                    else
                    {
                        ExaminedAgentGroups.Add(GUBPNodes[NodeToDo].Node.AgentSharingGroup);
                        foreach (string ChainNode in SortedAgentGroupChains[GUBPNodes[NodeToDo].Node.AgentSharingGroup])
                        {
                            foreach (string Dep in GUBPNodes[ChainNode].Node.FullNamesOfDependencies)
                            {
                                if (!GUBPNodes.ContainsKey(Dep))
                                {
                                    throw new AutomationException("Dependency {0} node found.", Dep);
                                }
                                if (!SortedAgentGroupChains[GUBPNodes[NodeToDo].Node.AgentSharingGroup].Contains(Dep) && NodesToDo.Contains(Dep))
                                {
                                    bReady = false;
                                    break;
                                }
                            }
                            if (!bReady)
                            {
                                NonReadyAgentGroups.Add(GUBPNodes[NodeToDo].Node.AgentSharingGroup);
                                break;
                            }
                            foreach (string Dep in GUBPNodes[ChainNode].Node.FullNamesOfPseudosependencies)
                            {
                                if (!GUBPNodes.ContainsKey(Dep))
                                {
                                    throw new AutomationException("Pseudodependency {0} node found.", Dep);
                                }
                                if (!SortedAgentGroupChains[GUBPNodes[NodeToDo].Node.AgentSharingGroup].Contains(Dep) && NodesToDo.Contains(Dep))
                                {
                                    bPseudoReady = false;
                                    NonPeudoReadyAgentGroups.Add(GUBPNodes[NodeToDo].Node.AgentSharingGroup);
                                    break;
                                }
                            }
                        }
                    }
                }
                else
                {
                    foreach (string Dep in GUBPNodes[NodeToDo].Node.FullNamesOfDependencies)
                    {
                        if (!GUBPNodes.ContainsKey(Dep))
                        {
                            throw new AutomationException("Dependency {0} node found.", Dep);
                        }
                        if (NodesToDo.Contains(Dep))
                        {
                            bReady = false;
                            break;
                        }
                    }
                    foreach (string Dep in GUBPNodes[NodeToDo].Node.FullNamesOfPseudosependencies)
                    {
                        if (!GUBPNodes.ContainsKey(Dep))
                        {
                            throw new AutomationException("Pseudodependency {0} node found.", Dep);
                        }
                        if (NodesToDo.Contains(Dep))
                        {
                            bPseudoReady = false;
                            break;
                        }
                    }
					foreach (string Dep in GUBPNodes[NodeToDo].Node.CompletedDependencies)
					{
						if (!GUBPNodes.ContainsKey(Dep))
						{
							throw new AutomationException("Completed Dependency {0} node found.", Dep);
						}
						if (NodesToDo.Contains(Dep))
						{
							bCompReady = false;
							break;
						}
					}
                }
                float Priority = GUBPNodes[NodeToDo].Node.Priority();

                if (bReady && BestNode != "")
                {
                    if (String.Compare(GetControllingTriggerDotName(BestNode, GUBPNodesControllingTriggerDotName, GUBPNodesControllingTrigger), GetControllingTriggerDotName(NodeToDo, GUBPNodesControllingTriggerDotName, GUBPNodesControllingTrigger)) < 0) //sorted by controlling trigger
                    {
                        bReady = false;
                    }
                    else if (String.Compare(GetControllingTriggerDotName(BestNode, GUBPNodesControllingTriggerDotName, GUBPNodesControllingTrigger), GetControllingTriggerDotName(NodeToDo, GUBPNodesControllingTriggerDotName, GUBPNodesControllingTrigger)) == 0) //sorted by controlling trigger
                    {
                        if (GUBPNodes[BestNode].Node.IsSticky() && !GUBPNodes[NodeToDo].Node.IsSticky()) //sticky nodes first
                        {
                            bReady = false;
                        }
                        else if (GUBPNodes[BestNode].Node.IsSticky() == GUBPNodes[NodeToDo].Node.IsSticky())
                        {
                            if (BestPseudoReady && !bPseudoReady)
                            {
                                bReady = false;
                            }
                            else if (BestPseudoReady == bPseudoReady)
                            {
                                bool IamLateTrigger = !DoNotConsiderCompletion && GUBPNodes[NodeToDo].Node.TriggerNode() && NodeToDo != ExplicitTrigger && !NodeIsAlreadyComplete(GUBPNodesCompleted, NodeToDo, LocalOnly);
                                bool BestIsLateTrigger = !DoNotConsiderCompletion && GUBPNodes[BestNode].Node.TriggerNode() && BestNode != ExplicitTrigger && !NodeIsAlreadyComplete(GUBPNodesCompleted, BestNode, LocalOnly);
                                if (BestIsLateTrigger && !IamLateTrigger)
                                {
                                    bReady = false;
                                }
                                else if (BestIsLateTrigger == IamLateTrigger)
                                {

                                    if (Priority < BestPriority)
                                    {
                                        bReady = false;
                                    }
                                    else if (Priority == BestPriority)
                                    {
                                        if (BestNode.CompareTo(NodeToDo) < 0)
                                        {
                                            bReady = false;
                                        }
                                    }
									if (!bCompReady)
									{
										bReady = false;
									}
                                }
                            }
                        }
                    }
                }
                if (bReady)
                {
                    BestPriority = Priority;
                    BestNode = NodeToDo;
                    BestPseudoReady = bPseudoReady;
                    bProgressMade = true;
                }
            }
            if (bProgressMade)
            {
                if (!SubSort && GUBPNodes[BestNode].Node.AgentSharingGroup != "")
                {
                    foreach (string ChainNode in SortedAgentGroupChains[GUBPNodes[BestNode].Node.AgentSharingGroup])
                    {
                        OrdereredToDo.Add(ChainNode);
                        NodesToDo.Remove(ChainNode);
                    }
                }
                else
                {
                    OrdereredToDo.Add(BestNode);
                    NodesToDo.Remove(BestNode);
                }
            }

            if (!bProgressMade && NodesToDo.Count > 0)
            {
                Log("Cycle in GUBP, could not resolve:");
                foreach (string NodeToDo in NodesToDo)
                {
                    string Deps = "";
                    if (!SubSort && GUBPNodes[NodeToDo].Node.AgentSharingGroup != "")
                    {
                        foreach (string ChainNode in SortedAgentGroupChains[GUBPNodes[NodeToDo].Node.AgentSharingGroup])
                        {
                            foreach (string Dep in GUBPNodes[ChainNode].Node.FullNamesOfDependencies)
                            {
                                if (!SortedAgentGroupChains[GUBPNodes[NodeToDo].Node.AgentSharingGroup].Contains(Dep) && NodesToDo.Contains(Dep))
                                {
                                    Deps = Deps + Dep + "[" + ChainNode + "->" + GUBPNodes[NodeToDo].Node.AgentSharingGroup + "]" + " ";
                                }
                            }
                        }
                    }
                    foreach (string Dep in GUBPNodes[NodeToDo].Node.FullNamesOfDependencies)
                    {
                        if (NodesToDo.Contains(Dep))
                        {
                            Deps = Deps + Dep + " ";
                        }
                    }
                    foreach (string Dep in GUBPNodes[NodeToDo].Node.FullNamesOfPseudosependencies)
                    {
                        if (NodesToDo.Contains(Dep))
                        {
                            Deps = Deps + Dep + " ";
                        }
                    }
                    Log("  {0}    deps: {1}", NodeToDo, Deps);
                }
                throw new AutomationException("Cycle in GUBP");
            }
        }
        if (!SubSort)
        {
            double BuildDuration = (DateTime.UtcNow - StartTime).TotalMilliseconds;
            Log("Took {0}s to sort {1} nodes", BuildDuration / 1000, OrdereredToDo.Count);
        }

        return OrdereredToDo;
    }

    string GetJobStepPath(string Dep)
    {
        if (Dep != "Noop" && GUBPNodes[Dep].Node.AgentSharingGroup != "")
        {
            return "jobSteps[" + GUBPNodes[Dep].Node.AgentSharingGroup + "]/jobSteps[" + Dep + "]";
        }
        return "jobSteps[" + Dep + "]";
    }
    string GetJobStep(string ParentPath, string Dep)
    {
        return ParentPath + "/" + GetJobStepPath(Dep);
    }

    void UpdateNodeHistory(Dictionary<string, NodeHistory> GUBPNodesHistory, string Node, string CLString)
    {
        if (GUBPNodes[Node].Node.RunInEC() && !GUBPNodes[Node].Node.TriggerNode() && CLString != "")
        {
            string GameNameIfAny = GUBPNodes[Node].Node.GameNameIfAnyForTempStorage();
            string NodeStoreWildCard = StoreName.Replace(CLString, "*") + "-" + GUBPNodes[Node].Node.GetFullName();
            NodeHistory History = new NodeHistory();

            History.AllStarted = ConvertCLToIntList(TempStorage.FindTempStorageManifests(CmdEnv, NodeStoreWildCard + StartedTempStorageSuffix, false, true, GameNameIfAny));
            History.AllSucceeded = ConvertCLToIntList(TempStorage.FindTempStorageManifests(CmdEnv, NodeStoreWildCard + SucceededTempStorageSuffix, false, true, GameNameIfAny));
            History.AllFailed = ConvertCLToIntList(TempStorage.FindTempStorageManifests(CmdEnv, NodeStoreWildCard + FailedTempStorageSuffix, false, true, GameNameIfAny));

            if (History.AllFailed.Count > 0)
            {
                History.LastFailed = History.AllFailed[History.AllFailed.Count - 1];
            }
            if (History.AllSucceeded.Count > 0)
            {
                History.LastSucceeded = History.AllSucceeded[History.AllSucceeded.Count - 1];

                foreach (int Failed in History.AllFailed)
                {
                    if (Failed > History.LastSucceeded)
                    {
                        History.Failed.Add(Failed);
                        History.FailedString = GUBPNode.MergeSpaceStrings(History.FailedString, String.Format("{0}", Failed));
                    }
                }
                foreach (int Started in History.AllStarted)
                {
                    if (Started > History.LastSucceeded && !History.Failed.Contains(Started))
                    {
                        History.InProgress.Add(Started);
                        History.InProgressString = GUBPNode.MergeSpaceStrings(History.InProgressString, String.Format("{0}", Started));
                    }
                }                
            }
			if (GUBPNodesHistory.ContainsKey(Node))
			{
				GUBPNodesHistory.Remove(Node);
			}
			GUBPNodesHistory.Add(Node, History);
        }
    }
	void GetFailureEmails(Dictionary<string, NodeHistory> GUBPNodesHistory, string NodeToDo, string CLString, bool OnlyLateUpdates = false)
	{
        string EMails;
        string FailCauserEMails = "";
        string EMailNote = "";
        bool SendSuccessForGreenAfterRed = false;
        int NumPeople = 0;
        if (GUBPNodesHistory.ContainsKey(NodeToDo))
        {
            NodeHistory History = GUBPNodesHistory[NodeToDo];
			RunECTool(String.Format("setProperty \"/myWorkflow/LastGreen/{0}\" \"{1}\"", NodeToDo, History.LastSucceeded), true);
			RunECTool(String.Format("setProperty \"/myWorkflow/LastGreen/{0}\" \"{1}\"", NodeToDo, History.FailedString), true);

            if (History.LastSucceeded > 0 && History.LastSucceeded < P4Env.Changelist)
            {
                int LastNonDuplicateFail = P4Env.Changelist;
                try
                {
                    if (OnlyLateUpdates)
                    {
                        LastNonDuplicateFail = FindLastNonDuplicateFail(GUBPNodesHistory, NodeToDo, CLString);
                        if (LastNonDuplicateFail < P4Env.Changelist)
                        {
                            Log("*** Red-after-red spam reduction, changed CL {0} to CL {1} because the errors didn't change.", P4Env.Changelist, LastNonDuplicateFail);
                        }
                    }
                }
                catch (Exception Ex)
                {
                    LastNonDuplicateFail = P4Env.Changelist;
                    Log(System.Diagnostics.TraceEventType.Warning, "Failed to FindLastNonDuplicateFail.");
                    Log(System.Diagnostics.TraceEventType.Warning, LogUtils.FormatException(Ex));
                }

                List<P4Connection.ChangeRecord> ChangeRecords = GetChanges(History.LastSucceeded, LastNonDuplicateFail, History.LastSucceeded);
                foreach (P4Connection.ChangeRecord Record in ChangeRecords)
                {
                    FailCauserEMails = GUBPNode.MergeSpaceStrings(FailCauserEMails, Record.UserEmail);
                }
                if (!String.IsNullOrEmpty(FailCauserEMails))
                {
                    NumPeople++;
                    foreach (char AChar in FailCauserEMails.ToCharArray())
                    {
                        if (AChar == ' ')
                        {
                            NumPeople++;
                        }
                    }
                    if (NumPeople > 50)
                    {
                        EMailNote = String.Format("This step has been broken for more than 50 changes. It last succeeded at CL {0}. ", History.LastSucceeded);
                    }
                }
            }
            else if (History.LastSucceeded <= 0)
            {
                EMailNote = String.Format("This step has been broken for more than a few days, so there is no record of it ever succeeding. ");
            }
            if (EMailNote != "" && !String.IsNullOrEmpty(History.FailedString))
            {
                EMailNote += String.Format("It has failed at CLs {0}. ", History.FailedString);
            }
            if (EMailNote != "" && !String.IsNullOrEmpty(History.InProgressString))
            {
                EMailNote += String.Format("These CLs are being built right now {0}. ", History.InProgressString);
            }
            if (History.LastSucceeded > 0 && History.LastSucceeded < P4Env.Changelist && History.LastFailed > History.LastSucceeded && History.LastFailed < P4Env.Changelist)
            {
                SendSuccessForGreenAfterRed = ParseParam("CIS");
            }
        }
        else
        {
			RunECTool(String.Format("setProperty \"/myWorkflow/LastGreen/{0}\" \"{1}\"", NodeToDo, "0"));
			RunECTool(String.Format("setProperty \"/myWorkflow/RedsSince/{0}\" \"{1}\"", NodeToDo, ""));
        }
		RunECTool(String.Format("setProperty \"/myWorkflow/FailCausers/{0}\" \"{1}\"", NodeToDo, FailCauserEMails));
		RunECTool(String.Format("setProperty \"/myWorkflow/EmailNotes/{0}\" \"{1}\"", NodeToDo, EMailNote));
        {
            string AdditionalEmails = "";
			string Causers = "";
            if (ParseParam("CIS") && !GUBPNodes[NodeToDo].Node.SendSuccessEmail() && !GUBPNodes[NodeToDo].Node.TriggerNode())
            {
				Causers = FailCauserEMails;
           }
            string AddEmails = ParseParamValue("AddEmails");
            if (!String.IsNullOrEmpty(AddEmails))
            {
                AdditionalEmails = GUBPNode.MergeSpaceStrings(AddEmails, AdditionalEmails);
            }
			EMails = GetEMailListForNode(this, NodeToDo, AdditionalEmails, Causers);
			RunECTool(String.Format("setProperty \"/myWorkflow/FailEmails/{0}\" \"{1}\"", NodeToDo, EMails));            
        }
		if (GUBPNodes[NodeToDo].Node.SendSuccessEmail() || SendSuccessForGreenAfterRed)
		{
			RunECTool(String.Format("setProperty \"/myWorkflow/SendSuccessEmail/{0}\" \"{1}\"", NodeToDo, "1"));
		}
		else
		{
			RunECTool(String.Format("setProperty \"/myWorkflow/SendSuccessEmail/{0}\" \"{1}\"", NodeToDo, "0"));
		}
	}

    bool HashSetEqual(HashSet<string> A, HashSet<string> B)
    {
        if (A.Count != B.Count)
        {
            return false;
        }
        foreach (string Elem in A)
        {
            if (!B.Contains(Elem))
            {
                return false;
            }
        }
        foreach (string Elem in B)
        {
            if (!A.Contains(Elem))
            {
                return false;
            }
        }
        return true;
    }

    int FindLastNonDuplicateFail(Dictionary<string, NodeHistory> GUBPNodesHistory, string NodeToDo, string CLString)
    {
        NodeHistory History = GUBPNodesHistory[NodeToDo];
        int Result = P4Env.Changelist;

        string GameNameIfAny = GUBPNodes[NodeToDo].Node.GameNameIfAnyForTempStorage();
        string NodeStore = StoreName + "-" + GUBPNodes[NodeToDo].Node.GetFullName() + FailedTempStorageSuffix;

        List<int> BackwardsFails = new List<int>(History.AllFailed);
        BackwardsFails.Add(P4Env.Changelist);
        BackwardsFails.Sort();
        BackwardsFails.Reverse();
        HashSet<string> CurrentErrors = null;
        foreach (int CL in BackwardsFails)
        {
            if (CL > P4Env.Changelist)
            {
                continue;
            }
            if (CL <= History.LastSucceeded)
            {
                break;
            }
            string ThisNodeStore = NodeStore.Replace(CLString, String.Format("{0}", CL));
            TempStorage.DeleteLocalTempStorage(CmdEnv, ThisNodeStore, true); // these all clash locally, which is fine we just retrieve them from shared

            List<string> Files = null;
            try
            {
                bool WasLocal;
                Files = TempStorage.RetrieveFromTempStorage(CmdEnv, ThisNodeStore, out WasLocal, GameNameIfAny); // this will fail on our CL if we didn't fail or we are just setting up the branch
            }
            catch (Exception)
            {
            }
            if (Files == null)
            {
                continue;
            }
            if (Files.Count != 1)
            {
                throw new AutomationException("Unexpected number of files for fail record {0}", Files.Count);
            }
            string ErrorFile = Files[0];
            HashSet<string> ThisErrors = ECJobPropsUtils.ErrorsFromProps(ErrorFile);
            if (CurrentErrors == null)
            {
                CurrentErrors = ThisErrors;
            }
            else
            {
                if (CurrentErrors.Count == 0 || !HashSetEqual(CurrentErrors, ThisErrors))
                {
                    break;
                }
                Result = CL;
            }
        }
        return Result;
    }
    List<string> GetECPropsForNode(string NodeToDo, string CLString, out string EMails, bool OnlyLateUpdates = false)
    {
        List<string> ECProps = new List<string>();
        EMails = "";		
		string AdditonalEmails = "";
		string Causers = "";				
		string AddEmails = ParseParamValue("AddEmails");
		if (!String.IsNullOrEmpty(AddEmails))
		{
			AdditonalEmails = GUBPNode.MergeSpaceStrings(AddEmails, AdditonalEmails);
		}
		EMails = GetEMailListForNode(this, NodeToDo, AdditonalEmails, Causers);
		ECProps.Add("FailEmails/" + NodeToDo + "=" + EMails);
	
		if (!OnlyLateUpdates)
		{
			string AgentReq = GUBPNodes[NodeToDo].Node.ECAgentString();
			if(ParseParamValue("AgentOverride") != "" && !GUBPNodes[NodeToDo].Node.GetFullName().Contains("OnMac"))
			{
				AgentReq = ParseParamValue("AgentOverride");
			}
			ECProps.Add(string.Format("AgentRequirementString/{0}={1}", NodeToDo, AgentReq));
			ECProps.Add(string.Format("RequiredMemory/{0}={1}", NodeToDo, GUBPNodes[NodeToDo].Node.AgentMemoryRequirement(this)));
			ECProps.Add(string.Format("Timeouts/{0}={1}", NodeToDo, GUBPNodes[NodeToDo].Node.TimeoutInMinutes()));
			ECProps.Add(string.Format("JobStepPath/{0}={1}", NodeToDo, GetJobStepPath(NodeToDo)));
		}
		
        return ECProps;
    }

    void UpdateECProps(string NodeToDo, string CLString)
    {
        try
        {
            Log("Updating node props for node {0}", NodeToDo);
			string EMails = "";
            List<string> Props = GetECPropsForNode(NodeToDo, CLString, out EMails, true);
            foreach (string Prop in Props)
            {
                string[] Parts = Prop.Split("=".ToCharArray());
                RunECTool(String.Format("setProperty \"/myWorkflow/{0}\" \"{1}\"", Parts[0], Parts[1]), true);
            }			
        }
        catch (Exception Ex)
        {
            Log(System.Diagnostics.TraceEventType.Warning, "Failed to UpdateECProps.");
            Log(System.Diagnostics.TraceEventType.Warning, LogUtils.FormatException(Ex));
        }
    }
	void UpdateECBuildTime(string NodeToDo, double BuildDuration)
	{
		try
		{
			Log("Updating duration prop for node {0}", NodeToDo);
			RunECTool(String.Format("setProperty \"/myWorkflow/NodeDuration/{0}\" \"{1}\"", NodeToDo, BuildDuration.ToString()));
			RunECTool(String.Format("setProperty \"/myJobStep/NodeDuration\" \"{0}\"", BuildDuration.ToString()));
		}
		catch (Exception Ex)
		{
			Log(System.Diagnostics.TraceEventType.Warning, "Failed to UpdateECBuildTime.");
			Log(System.Diagnostics.TraceEventType.Warning, LogUtils.FormatException(Ex));
		}
	}

    [Help("Runs one, several or all of the GUBP nodes")]
    [Help(typeof(UE4Build))]
    [Help("NoMac", "Toggle to exclude the Mac host platform, default is Win64+Mac+Linux")]
	[Help("NoLinux", "Toggle to exclude the Linux (PC, 64-bit) host platform, default is Win64+Mac+Linux")]
	[Help("NoPC", "Toggle to exclude the PC host platform, default is Win64+Mac+Linux")]
    [Help("CleanLocal", "delete the local temp storage before we start")]
    [Help("Store=", "Sets the name of the temp storage block, normally, this is built for you.")]
    [Help("StoreSuffix=", "Tacked onto a store name constructed from CL, branch, etc")]
    [Help("TimeIndex=", "An integer used to determine subsets to run based on DependentCISFrequencyQuantumShift")]
    [Help("UserTimeIndex=", "An integer used to determine subsets to run based on DependentCISFrequencyQuantumShift, this one overrides TimeIndex")]
    [Help("PreflightUID=", "A unique integer tag from EC used as part of the tempstorage, builds and label names to distinguish multiple attempts.")]
    [Help("Node=", "Nodes to process, -node=Node1+Node2+Node3, if no nodes or games are specified, defaults to all nodes.")]
    [Help("SetupNode=", "Like -Node, but only applies with CommanderJobSetupOnly")]
    [Help("RelatedToNode=", "Nodes to process, -RelatedToNode=Node1+Node2+Node3, use all nodes that either depend on these nodes or these nodes depend on them.")]
    [Help("SetupRelatedToNode=", "Like -RelatedToNode, but only applies with CommanderJobSetupOnly")]
    [Help("OnlyNode=", "Nodes to process NO dependencies, -OnlyNode=Node1+Node2+Node3, if no nodes or games are specified, defaults to all nodes.")]
    [Help("TriggerNode=", "Trigger Nodes to process, -triggernode=Node.")]
    [Help("Game=", "Games to process, -game=Game1+Game2+Game3, if no games or nodes are specified, defaults to all nodes.")]
    [Help("ListOnly", "List Nodes in this branch")]
    [Help("SaveGraph", "Save graph as an xml file")]
    [Help("CommanderJobSetupOnly", "Set up the EC branch info via ectool and quit")]
    [Help("FakeEC", "don't run ectool, rather just do it locally, emulating what EC would have done.")]
    [Help("Fake", "Don't actually build anything, just store a record of success as the build product for each node.")]
    [Help("AllPlatforms", "Regardless of what is installed on this machine, set up the graph for all platforms; true by default on build machines.")]
    [Help("SkipTriggers", "ignore all triggers")]
    [Help("CL", "force the CL to something, disregarding the P4 value.")]
    [Help("History", "Like ListOnly, except gives you a full history. Must have -P4 for this to work.")]
    [Help("Changes", "Like history, but also shows the P4 changes. Must have -P4 for this to work.")]
    [Help("AllChanges", "Like changes except includes changes before the last green. Must have -P4 for this to work.")]
    [Help("EmailOnly", "Only emails the folks given in the argument.")]
    [Help("AddEmails", "Add these space delimited emails too all email lists.")]
    [Help("ShowDependencies", "Show node dependencies.")]
    [Help("ShowECDependencies", "Show EC node dependencies instead.")]
    [Help("ShowECProc", "Show EC proc names.")]
    [Help("BuildRocket", "Build in rocket mode.")]
    [Help("ShowECOnly", "Only show EC nodes.")]
    [Help("ECProject", "From EC, the name of the project, used to get a version number.")]
    [Help("CIS", "This is a CIS run, assign TimeIndex based on the history.")]
    [Help("ForceIncrementalCompile", "make sure all compiles are incremental")]
    [Help("AutomatedTesting", "Allow automated testing, currently disabled.")]
    [Help("StompCheck", "Look for stomped build products.")]

    public override void ExecuteBuild()
    {
        Log("************************* GUBP");
        string PreflightShelveCLString = GetEnvVar("uebp_PreflightShelveCL");
        if ((!String.IsNullOrEmpty(PreflightShelveCLString) && IsBuildMachine) || ParseParam("PreflightTest"))
        {
            Log("**** Preflight shelve {0}", PreflightShelveCLString);
            if (!String.IsNullOrEmpty(PreflightShelveCLString))
            {
                PreflightShelveCL = int.Parse(PreflightShelveCLString);
                if (PreflightShelveCL < 2000000)
                {
                    throw new AutomationException(String.Format( "{0} does not look like a CL", PreflightShelveCL));
                }
            }
            bPreflightBuild = true;
        }
        
        HostPlatforms = new List<UnrealTargetPlatform>();
        if (!ParseParam("NoPC"))
        {
            HostPlatforms.Add(UnrealTargetPlatform.Win64);
        }
        if (P4Enabled)
        {
            BranchName = P4Env.BuildRootP4;
        }
		else
		{ 
			BranchName = ParseParamValue("BranchName", "");
        }
        BranchOptions = GetBranchOptions(BranchName);
        bool WithMac = !BranchOptions.PlatformsToRemove.Contains(UnrealTargetPlatform.Mac);
        if (ParseParam("NoMac"))
        {
            WithMac = false;
        }
        if (WithMac)
        {
            HostPlatforms.Add(UnrealTargetPlatform.Mac);
        }

		bool WithLinux = !BranchOptions.PlatformsToRemove.Contains(UnrealTargetPlatform.Linux);
		bool WithoutLinux = ParseParam("NoLinux");
		// @TODO: exclude temporarily unless running on a Linux machine to prevent spurious GUBP failures
		if (UnrealBuildTool.BuildHostPlatform.Current.Platform != UnrealTargetPlatform.Linux || ParseParam("NoLinux"))
		{
			WithLinux = false;
		}
		if (WithLinux)
		{
			HostPlatforms.Add(UnrealTargetPlatform.Linux);
		}

        bForceIncrementalCompile = ParseParam("ForceIncrementalCompile");
        bool bNoAutomatedTesting = ParseParam("NoAutomatedTesting") || BranchOptions.bNoAutomatedTesting;		
        StoreName = ParseParamValue("Store");
        string StoreSuffix = ParseParamValue("StoreSuffix", "");

        if (bPreflightBuild)
        {
            int PreflightUID = ParseParamInt("PreflightUID", 0);
            PreflightMangleSuffix = String.Format("-PF-{0}-{1}", PreflightShelveCL, PreflightUID);
            StoreSuffix = StoreSuffix + PreflightMangleSuffix;
        }
        CL = ParseParamInt("CL", 0);
        bool bCleanLocalTempStorage = ParseParam("CleanLocal");
        bool bChanges = ParseParam("Changes") || ParseParam("AllChanges");
        bool bHistory = ParseParam("History") || bChanges;
        bool bListOnly = ParseParam("ListOnly") || bHistory;
        bool bSkipTriggers = ParseParam("SkipTriggers");
        bFake = ParseParam("fake");
        bool bFakeEC = ParseParam("FakeEC");
        int TimeIndex = ParseParamInt("TimeIndex", 0);
        if (TimeIndex == 0)
        {
            TimeIndex = ParseParamInt("UserTimeIndex", 0);
        }

        bNoIOSOnPC = HostPlatforms.Contains(UnrealTargetPlatform.Mac);

        bool bSaveSharedTempStorage = false;

        if (bHistory && !P4Enabled)
        {
            throw new AutomationException("-Changes and -History require -P4.");
        }
        bool LocalOnly = true;
        string CLString = "";
        if (String.IsNullOrEmpty(StoreName))
        {
            if (P4Enabled)
            {
                if (CL == 0)
                {
                    CL = P4Env.Changelist;
                }
                CLString = String.Format("{0}", CL);
                StoreName = P4Env.BuildRootEscaped + "-" + CLString;
                bSaveSharedTempStorage = CommandUtils.IsBuildMachine;
                LocalOnly = false;
            }
            else
            {
                StoreName = "TempLocal";
                bSaveSharedTempStorage = false;
            }
        }
        StoreName = StoreName + StoreSuffix;
        if (bFakeEC)
        {
            LocalOnly = true;
        } 
        if (bSaveSharedTempStorage)
        {
            if (!TempStorage.HaveSharedTempStorage(true))
            {
                throw new AutomationException("Request to save to temp storage, but {0} is unavailable.", TempStorage.UE4TempStorageDirectory());
            }
            bSignBuildProducts = true;
        }
        else if (!LocalOnly && !TempStorage.HaveSharedTempStorage(false))
        {
            LogWarning("Looks like we want to use shared temp storage, but since we don't have it, we won't use it.");
            LocalOnly = true;
        }

        bool CommanderSetup = ParseParam("CommanderJobSetupOnly");
        string ExplicitTrigger = "";
        if (CommanderSetup)
        {
            ExplicitTrigger = ParseParamValue("TriggerNode");
            if (ExplicitTrigger == null)
            {
                ExplicitTrigger = "";
            }
        }

        if (ParseParam("CIS") && ExplicitTrigger == "" && CommanderSetup) // explicit triggers will already have a time index assigned
        {
			TimeIndex = UpdateCISCounter();
            Log("Setting TimeIndex to {0}", TimeIndex);
        }

        LogVerbose("************************* CL:			                    {0}", CL);
		LogVerbose("************************* P4Enabled:			            {0}", P4Enabled);
        foreach (UnrealTargetPlatform HostPlatform in HostPlatforms)
        {
			LogVerbose("*************************   HostPlatform:		            {0}", HostPlatform.ToString()); 
        }
		LogVerbose("************************* StoreName:		                {0}", StoreName.ToString());
		LogVerbose("************************* bCleanLocalTempStorage:		    {0}", bCleanLocalTempStorage);
		LogVerbose("************************* bSkipTriggers:		            {0}", bSkipTriggers);
		LogVerbose("************************* bSaveSharedTempStorage:		    {0}", bSaveSharedTempStorage);
		LogVerbose("************************* bSignBuildProducts:		        {0}", bSignBuildProducts);
		LogVerbose("************************* bFake:           		        {0}", bFake);
		LogVerbose("************************* bFakeEC:           		        {0}", bFakeEC);
		LogVerbose("************************* bHistory:           		        {0}", bHistory);
		LogVerbose("************************* TimeIndex:           		    {0}", TimeIndex);        


        GUBPNodes = new Dictionary<string, NodeInfo>();        
        Branch = new BranchInfo(HostPlatforms);        

		AddNodesForBranch(TimeIndex, bNoAutomatedTesting);

		ValidateDependencyNames();

        if (bCleanLocalTempStorage)  // shared temp storage can never be wiped
        {
            TempStorage.DeleteLocalTempStorageManifests(CmdEnv);
        }

		Dictionary<string, string> GUBPNodesControllingTrigger = new Dictionary<string, string>();
        Dictionary<string, string> GUBPNodesControllingTriggerDotName = new Dictionary<string, string>();

		Dictionary<string, string> FullNodeDependedOnBy = new Dictionary<string, string>();
		Dictionary<string, string> FullNodeDependentPromotions = new Dictionary<string, string>();		
		List<string> SeparatePromotables = new List<string>();
        {
            foreach (KeyValuePair<string, NodeInfo> NodeToDo in GUBPNodes)
            {
                if (GUBPNodes[NodeToDo.Key].Node.IsSeparatePromotable())
                {
                    SeparatePromotables.Add(GUBPNodes[NodeToDo.Key].Node.GetFullName());
                    List<string> Dependencies = new List<string>();
                    Dependencies = GetECDependencies(NodeToDo.Key);
                    foreach (string Dep in Dependencies)
                    {
                        if (!GUBPNodes.ContainsKey(Dep))
                        {
                            throw new AutomationException("Node {0} is not in the graph.  It is a dependency of {1}.", Dep, NodeToDo);
                        }
                        if (!GUBPNodes[Dep].Node.IsPromotableAggregate())
                        {
                            if (!GUBPNodes[Dep].Node.DependentPromotions.Contains(NodeToDo.Key))
                            {
                                GUBPNodes[Dep].Node.DependentPromotions.Add(NodeToDo.Key);
                            }
                        }
                    }
                }
            }

			// Compute all the frequencies
			Dictionary<string, int> FrequencyOverrides = ApplyFrequencyBarriers();
            foreach (KeyValuePair<string, NodeInfo> NodeToDo in GUBPNodes)
            {
                ComputeDependentCISFrequencyQuantumShift(NodeToDo.Key, FrequencyOverrides);
            }

            foreach (KeyValuePair<string, NodeInfo> NodeToDo in GUBPNodes)
            {
                List<string> Deps = GUBPNodes[NodeToDo.Key].Node.DependentPromotions;
                string All = "";
                foreach (string Dep in Deps)
                {
                    if (All != "")
                    {
                        All += " ";
                    }
                    All += Dep;
                }
                FullNodeDependentPromotions.Add(NodeToDo.Key, All);
            }
        }	

        Dictionary<string, string> FullNodeList;
        Dictionary<string, string> FullNodeDirectDependencies;
		GetFullNodeListAndDirectDependencies(GUBPNodesControllingTrigger, GUBPNodesControllingTriggerDotName, out FullNodeList, out FullNodeDirectDependencies);

		Dictionary<string, int> FullNodeListSortKey = GetDisplayOrder(FullNodeList.Keys.ToList(), FullNodeDirectDependencies, GUBPNodes);

		bool bOnlyNode;
		HashSet<string> NodesToDo;
		ParseNodesToDo(WithoutLinux, TimeIndex, CommanderSetup, FullNodeDependedOnBy, out bOnlyNode, out NodesToDo);

        if (CommanderSetup)
        {
            if (!String.IsNullOrEmpty(ExplicitTrigger))
            {
                bool bFoundIt = false;
                foreach (KeyValuePair<string, NodeInfo> Node in GUBPNodes)
                {
                    if (Node.Value.Node.GetFullName().Equals(ExplicitTrigger, StringComparison.InvariantCultureIgnoreCase))
                    {
                        if (Node.Value.Node.TriggerNode() && Node.Value.Node.RunInEC())
                        {
                            Node.Value.Node.SetAsExplicitTrigger();
                            bFoundIt = true;
                            break;
                        }
                    }
                }
                if (!bFoundIt)
                {
                    throw new AutomationException("Could not find trigger node named {0}", ExplicitTrigger);
                }
            }
            else
            {
                if (bSkipTriggers)
                {
                    foreach (KeyValuePair<string, NodeInfo> Node in GUBPNodes)
                    {
                        if (Node.Value.Node.TriggerNode() && Node.Value.Node.RunInEC())
                        {
                            Node.Value.Node.SetAsExplicitTrigger();
                        }
                    }
                }
            }
        }
		if (bPreflightBuild)
		{
			LogVerbose("Culling triggers and downstream for preflight builds ");
			HashSet<string> NewNodesToDo = new HashSet<string>();
			foreach (string NodeToDo in NodesToDo)
			{
				string TriggerDot = GetControllingTriggerDotName(NodeToDo, GUBPNodesControllingTriggerDotName, GUBPNodesControllingTrigger);
				if (TriggerDot == "" && !GUBPNodes[NodeToDo].Node.TriggerNode())
				{
					LogVerbose("  Keeping {0}", NodeToDo);
					NewNodesToDo.Add(NodeToDo);
				}
				else
				{
					LogVerbose("  Rejecting {0}", NodeToDo);
				}
			}
			NodesToDo = NewNodesToDo;
		}

        Dictionary<string, bool> GUBPNodesCompleted = new Dictionary<string, bool>();
        Dictionary<string, NodeHistory> GUBPNodesHistory = new Dictionary<string, NodeHistory>();

        LogVerbose("******* Caching completion");
        {
            DateTime StartTime = DateTime.UtcNow;
            foreach (string Node in NodesToDo)
            {
                LogVerbose("** {0}", Node);
                NodeIsAlreadyComplete(GUBPNodesCompleted, Node, LocalOnly); // cache these now to avoid spam later
                GetControllingTriggerDotName(Node, GUBPNodesControllingTriggerDotName, GUBPNodesControllingTrigger);
            }
            double BuildDuration = (DateTime.UtcNow - StartTime).TotalMilliseconds;
			LogVerbose("Took {0}s to cache completion for {1} nodes", BuildDuration / 1000, NodesToDo.Count);
        }
        /*if (CLString != "" && StoreName.Contains(CLString) && !ParseParam("NoHistory"))
        {
            Log("******* Updating history");
            var StartTime = DateTime.UtcNow;
            foreach (var Node in NodesToDo)
            {
                if (!NodeIsAlreadyComplete(Node, LocalOnly))
                {
                    UpdateNodeHistory(Node, CLString);
                }				
            }
            var BuildDuration = (DateTime.UtcNow - StartTime).TotalMilliseconds;
            Log("Took {0}s to get history for {1} nodes", BuildDuration / 1000, NodesToDo.Count);
        }*/

        List<string> OrdereredToDo = TopologicalSort(NodesToDo, GUBPNodesCompleted, GUBPNodesControllingTriggerDotName, GUBPNodesControllingTrigger, ExplicitTrigger, LocalOnly);

		List<string> UnfinishedTriggers = FindUnfinishedTriggers(bSkipTriggers, LocalOnly, ExplicitTrigger, GUBPNodesCompleted, OrdereredToDo);

        LogVerbose("*********** Desired And Dependent Nodes, in order.");
        PrintNodes(this, OrdereredToDo, GUBPNodesCompleted, GUBPNodesHistory, GUBPNodesControllingTriggerDotName, GUBPNodesControllingTrigger, LocalOnly, UnfinishedTriggers);		
        //check sorting
		CheckSortOrder(LocalOnly, GUBPNodesCompleted, OrdereredToDo);

        string FakeFail = ParseParamValue("FakeFail");
        if (CommanderSetup)
        {
			DoCommanderSetup(TimeIndex, bSkipTriggers, bFakeEC, LocalOnly, CLString, ExplicitTrigger, GUBPNodesControllingTrigger, GUBPNodesControllingTriggerDotName, FullNodeList, FullNodeDirectDependencies, FullNodeDependedOnBy, FullNodeDependentPromotions, SeparatePromotables, FullNodeListSortKey, GUBPNodesCompleted, GUBPNodesHistory, OrdereredToDo, UnfinishedTriggers, FakeFail);
            Log("Commander setup only, done.");            
            PrintRunTime();
            return;

        }
        if (ParseParam("SaveGraph"))
        {
            SaveGraphVisualization(OrdereredToDo);
        }
        if (bListOnly)
        {
            Log("List only, done.");
            return;
        }    
		ExecuteNodes(OrdereredToDo, bOnlyNode, bFakeEC, LocalOnly, bSaveSharedTempStorage, GUBPNodesCompleted, GUBPNodesHistory, CLString, FakeFail);
	}

	private Dictionary<string, int> ApplyFrequencyBarriers()
	{
		// Make sure that everything that's listed as a frequency barrier is completed with the given interval
		Dictionary<string, int> FrequencyOverrides = new Dictionary<string, int>();
		foreach (KeyValuePair<string, sbyte> Barrier in BranchOptions.FrequencyBarriers)
		{
			// All the nodes which are dependencies of the barrier node
			HashSet<string> IncludedNodes = new HashSet<string> { Barrier.Key };

			// Find all the nodes which are indirect dependencies of this node
			List<string> SearchNodes = new List<string> { Barrier.Key };
			for (int Idx = 0; Idx < SearchNodes.Count; Idx++)
			{
				GUBPNode Node = GUBPNodes[SearchNodes[Idx]].Node;
				foreach (string DependencyName in Node.FullNamesOfDependencies.Union(Node.FullNamesOfPseudosependencies))
				{
					if (!IncludedNodes.Contains(DependencyName))
					{
						IncludedNodes.Add(DependencyName);
						SearchNodes.Add(DependencyName);
					}
				}
			}

			// Make sure that everything included in this list is before the cap, and everything not in the list is after it
			foreach (KeyValuePair<string, NodeInfo> NodePair in GUBPNodes)
			{
				if (IncludedNodes.Contains(NodePair.Key))
				{
					int Frequency;
					if (FrequencyOverrides.TryGetValue(NodePair.Key, out Frequency))
					{
						Frequency = Math.Min(Frequency, Barrier.Value);
					}
					else
					{
						Frequency = Barrier.Value;
					}
					FrequencyOverrides[NodePair.Key] = Frequency;
				}
			}
		}
		return FrequencyOverrides;
	}

	private void ParseNodesToDo(bool WithoutLinux, int TimeIndex, bool CommanderSetup, Dictionary<string, string> FullNodeDependedOnBy, out bool bOnlyNode, out HashSet<string> NodesToDo)
	{
		bOnlyNode = false;
		bool bRelatedToNode = false;
		NodesToDo = new HashSet<string>();

		{
			string NodeSpec = ParseParamValue("Node");
			if (String.IsNullOrEmpty(NodeSpec))
			{
				NodeSpec = ParseParamValue("RelatedToNode");
				if (!String.IsNullOrEmpty(NodeSpec))
				{
					bRelatedToNode = true;
				}
			}
			if (String.IsNullOrEmpty(NodeSpec) && CommanderSetup)
			{
				NodeSpec = ParseParamValue("SetupNode");
				if (String.IsNullOrEmpty(NodeSpec))
				{
					NodeSpec = ParseParamValue("SetupRelatedToNode");
					if (!String.IsNullOrEmpty(NodeSpec))
					{
						bRelatedToNode = true;
					}
				}
			}
			if (String.IsNullOrEmpty(NodeSpec))
			{
				NodeSpec = ParseParamValue("OnlyNode");
				if (!String.IsNullOrEmpty(NodeSpec))
				{
					bOnlyNode = true;
				}
			}
			if (!String.IsNullOrEmpty(NodeSpec))
			{
				List<string> Nodes = new List<string>(NodeSpec.Split('+'));
				foreach (string NodeArg in Nodes)
				{
					string NodeName = NodeArg.Trim();
					bool bFoundAnything = false;
					if (!String.IsNullOrEmpty(NodeName))
					{
						foreach (KeyValuePair<string, NodeInfo> Node in GUBPNodes)
						{
							if (Node.Value.Node.GetFullName().Equals(NodeArg, StringComparison.InvariantCultureIgnoreCase) ||
								Node.Value.Node.AgentSharingGroup.Equals(NodeArg, StringComparison.InvariantCultureIgnoreCase)
								)
							{
								if (!NodesToDo.Contains(Node.Key))
								{
									NodesToDo.Add(Node.Key);
								}
								bFoundAnything = true;
							}
						}
						if (!bFoundAnything)
						{
							throw new AutomationException("Could not find node named {0}", NodeName);
						}
					}
				}
			}
		}
		string GameSpec = ParseParamValue("Game");
		if (!String.IsNullOrEmpty(GameSpec))
		{
			List<string> Games = new List<string>(GameSpec.Split('+'));
			foreach (string GameArg in Games)
			{
				string GameName = GameArg.Trim();
				if (!String.IsNullOrEmpty(GameName))
				{
					foreach (BranchInfo.BranchUProject GameProj in Branch.CodeProjects)
					{
						if (GameProj.GameName.Equals(GameName, StringComparison.InvariantCultureIgnoreCase))
						{
							if (GameProj.Options(UnrealTargetPlatform.Win64).bIsPromotable)
							{
								NodesToDo.Add(GameAggregatePromotableNode.StaticGetFullName(GameProj));
							}
							foreach (KeyValuePair<string, NodeInfo> Node in GUBPNodes)
							{
								if (Node.Value.Node.GameNameIfAnyForTempStorage() == GameProj.GameName)
								{
									NodesToDo.Add(Node.Key);
								}
							}

							GameName = null;
						}
					}
					if (GameName != null)
					{
						foreach (BranchInfo.BranchUProject GameProj in Branch.NonCodeProjects)
						{
							if (GameProj.GameName.Equals(GameName, StringComparison.InvariantCultureIgnoreCase))
							{
								foreach (KeyValuePair<string, NodeInfo> Node in GUBPNodes)
								{
									if (Node.Value.Node.GameNameIfAnyForTempStorage() == GameProj.GameName)
									{
										NodesToDo.Add(Node.Key);
									}
								}
								GameName = null;
							}
						}
					}
					if (GameName != null)
					{
						throw new AutomationException("Could not find game named {0}", GameName);
					}
				}
			}
		}
		if (NodesToDo.Count == 0)
		{
			LogVerbose("No nodes specified, adding all nodes");
			foreach (KeyValuePair<string, NodeInfo> Node in GUBPNodes)
			{
				NodesToDo.Add(Node.Key);
			}
		}
		else if (TimeIndex != 0)
		{
			LogVerbose("Check to make sure we didn't ask for nodes that will be culled by time index");
			foreach (string NodeToDo in NodesToDo)
			{
				if (TimeIndex % (1 << GUBPNodes[NodeToDo].Node.DependentCISFrequencyQuantumShift()) != 0)
				{
					throw new AutomationException("You asked specifically for node {0}, but it is culled by the time quantum: TimeIndex = {1}, DependentCISFrequencyQuantumShift = {2}.", NodeToDo, TimeIndex, GUBPNodes[NodeToDo].Node.DependentCISFrequencyQuantumShift());
				}
			}
		}

		LogVerbose("Desired Nodes");
		foreach (string NodeToDo in NodesToDo)
		{
			LogVerbose("  {0}", NodeToDo);
		}
		// if we are doing related to, then find things that depend on the selected nodes
		if (bRelatedToNode)
		{
			bool bDoneWithDependencies = false;

			while (!bDoneWithDependencies)
			{
				bDoneWithDependencies = true;
				HashSet<string> Fringe = new HashSet<string>();
				foreach (KeyValuePair<string, NodeInfo> NodeToDo in GUBPNodes)
				{
					if (!NodesToDo.Contains(NodeToDo.Key))
					{
						foreach (string Dep in GUBPNodes[NodeToDo.Key].Node.FullNamesOfDependencies)
						{
							if (!GUBPNodes.ContainsKey(Dep))
							{
								throw new AutomationException("Node {0} is not in the graph. It is a dependency of {1}.", Dep, NodeToDo.Key);
							}
							if (NodesToDo.Contains(Dep))
							{
								Fringe.Add(NodeToDo.Key);
								bDoneWithDependencies = false;
							}
						}
						foreach (string Dep in GUBPNodes[NodeToDo.Key].Node.FullNamesOfPseudosependencies)
						{
							if (!GUBPNodes.ContainsKey(Dep))
							{
								throw new AutomationException("Node {0} is not in the graph. It is a pseudodependency of {1}.", Dep, NodeToDo.Key);
							}
						}
					}
				}
				NodesToDo.UnionWith(Fringe);
			}
		}

		// find things that our nodes depend on
		if (!bOnlyNode)
		{
			bool bDoneWithDependencies = false;

			while (!bDoneWithDependencies)
			{
				bDoneWithDependencies = true;
				HashSet<string> Fringe = new HashSet<string>();
				foreach (string NodeToDo in NodesToDo)
				{
					if (BranchOptions.NodesToRemovePseudoDependencies.Count > 0)
					{
						List<string> ListOfRetainedNodesToRemovePseudoDependencies = BranchOptions.NodesToRemovePseudoDependencies;
						foreach (string NodeToRemovePseudoDependencies in BranchOptions.NodesToRemovePseudoDependencies)
						{
							if (NodeToDo == NodeToRemovePseudoDependencies)
							{
								RemoveAllPseudodependenciesFromNode(NodeToDo);
								ListOfRetainedNodesToRemovePseudoDependencies.Remove(NodeToDo);
								break;
							}
						}
						BranchOptions.NodesToRemovePseudoDependencies = ListOfRetainedNodesToRemovePseudoDependencies;
					}
					foreach (string Dep in GUBPNodes[NodeToDo].Node.FullNamesOfDependencies)
					{
						if (!GUBPNodes.ContainsKey(Dep))
						{
							throw new AutomationException("Node {0} is not in the graph. It is a dependency of {1}.", Dep, NodeToDo);
						}
						if (!NodesToDo.Contains(Dep))
						{
							Fringe.Add(Dep);
							bDoneWithDependencies = false;
						}
					}
					foreach (string Dep in GUBPNodes[NodeToDo].Node.FullNamesOfPseudosependencies)
					{
						if (!GUBPNodes.ContainsKey(Dep))
						{
							throw new AutomationException("Node {0} is not in the graph. It is a pseudodependency of {1}.", Dep, NodeToDo);
						}
					}
				}
				NodesToDo.UnionWith(Fringe);
			}
		}
		if (TimeIndex != 0)
		{
			LogVerbose("Culling based on time index");
			HashSet<string> NewNodesToDo = new HashSet<string>();
			foreach (string NodeToDo in NodesToDo)
			{
				if (TimeIndex % (1 << GUBPNodes[NodeToDo].Node.DependentCISFrequencyQuantumShift()) == 0)
				{
					LogVerbose("  Keeping {0}", NodeToDo);
					NewNodesToDo.Add(NodeToDo);
				}
				else
				{
					LogVerbose("  Rejecting {0}", NodeToDo);
				}
			}
			NodesToDo = NewNodesToDo;
		}
		//Remove Plat if specified
		if (WithoutLinux)
		{
			HashSet<string> NewNodesToDo = new HashSet<string>();
			foreach (string NodeToDo in NodesToDo)
			{
				if (!GUBPNodes[NodeToDo].Node.GetFullName().Contains("Linux"))
				{
					NewNodesToDo.Add(NodeToDo);
				}
				else
				{
					LogVerbose(" Rejecting {0} because -NoLinux was requested", NodeToDo);
				}
			}
			NodesToDo = NewNodesToDo;
		}
		//find things that depend on our nodes and setup commander dictionary
		if (!bOnlyNode)
		{
			foreach (string NodeToDo in NodesToDo)
			{
				if (!GUBPNodes[NodeToDo].Node.IsAggregate() && !GUBPNodes[NodeToDo].Node.IsTest())
				{
					List<string> ECDependencies = new List<string>();
					ECDependencies = GetECDependencies(NodeToDo);
					foreach (string Dep in ECDependencies)
					{
						if (!GUBPNodes.ContainsKey(Dep))
						{
							throw new AutomationException("Node {0} is not in the graph. It is a dependency of {1}.", Dep, NodeToDo);
						}
						if (!GUBPNodes[Dep].Node.FullNamesOfDependedOn.Contains(NodeToDo))
						{
							GUBPNodes[Dep].Node.FullNamesOfDependedOn.Add(NodeToDo);
						}
					}
				}
			}
			foreach (string NodeToDo in NodesToDo)
			{
				List<string> Deps = GUBPNodes[NodeToDo].Node.FullNamesOfDependedOn;
				string All = "";
				foreach (string Dep in Deps)
				{
					if (All != "")
					{
						All += " ";
					}
					All += Dep;
				}
				FullNodeDependedOnBy.Add(NodeToDo, All);
			}
		}
	}

	private void GetFullNodeListAndDirectDependencies(Dictionary<string, string> GUBPNodesControllingTrigger, Dictionary<string, string> GUBPNodesControllingTriggerDotName, out Dictionary<string, string> FullNodeList, out Dictionary<string, string> FullNodeDirectDependencies)
	{
		FullNodeList = new Dictionary<string,string>();
		FullNodeDirectDependencies = new Dictionary<string,string>();

		Log("******* {0} GUBP Nodes", GUBPNodes.Count);
		List<string> SortedNodes = TopologicalSort(new HashSet<string>(GUBPNodes.Keys), null/*GUBPNodesCompleted*/, GUBPNodesControllingTriggerDotName, GUBPNodesControllingTrigger, LocalOnly: true, DoNotConsiderCompletion: true);
		string DependencyStart = DateTime.Now.ToString();
		foreach (string Node in SortedNodes)
		{
			string Note = GetControllingTriggerDotName(Node, GUBPNodesControllingTriggerDotName, GUBPNodesControllingTrigger);
			if (Note == "")
			{
				Note = CISFrequencyQuantumShiftString(Node);
			}
			if (Note == "")
			{
				Note = "always";
			}
			if (GUBPNodes[Node].Node.RunInEC())
			{
				List<string> Deps = GetECDependencies(Node);
				string All = "";
				foreach (string Dep in Deps)
				{
					if (All != "")
					{
						All += " ";
					}
					All += Dep;
				}
				LogVerbose("  {0}: {1}      {2}", Node, Note, All);
				FullNodeList.Add(Node, Note);
				FullNodeDirectDependencies.Add(Node, All);
			}
			else
			{
				LogVerbose("  {0}: {1} [Aggregate]", Node, Note);
			}
		}
		string DependencyFinish = DateTime.Now.ToString();
		PrintCSVFile(String.Format("UAT,GetDependencies,{0},{1}", DependencyStart, DependencyFinish));
	}

	private void ValidateDependencyNames()
	{
		foreach (KeyValuePair<string, NodeInfo> NodeToDo in GUBPNodes)
		{
			foreach (string Dep in GUBPNodes[NodeToDo.Key].Node.FullNamesOfDependencies)
			{
				if (!GUBPNodes.ContainsKey(Dep))
				{
					throw new AutomationException("Node {0} is not in the full graph. It is a dependency of {1}.", Dep, NodeToDo.Key);
				}
				if (Dep == NodeToDo.Key)
				{
					throw new AutomationException("Node {0} has a self arc.", NodeToDo.Key);
				}
			}
			foreach (string Dep in GUBPNodes[NodeToDo.Key].Node.FullNamesOfPseudosependencies)
			{
				if (!GUBPNodes.ContainsKey(Dep))
				{
					throw new AutomationException("Node {0} is not in the full graph. It is a pseudodependency of {1}.", Dep, NodeToDo.Key);
				}
				if (Dep == NodeToDo.Key)
				{
					throw new AutomationException("Node {0} has a self pseudoarc.", NodeToDo.Key);
				}
			}
		}
	}

	private int UpdateCISCounter()
	{
		if (!P4Enabled)
		{
			throw new AutomationException("Can't have -CIS without P4 support");
		}
		string P4IndexFileP4 = CombinePaths(PathSeparator.Slash, CommandUtils.P4Env.BuildRootP4, "Engine", "Build", "CISCounter.txt");
		string P4IndexFileLocal = CombinePaths(CmdEnv.LocalRoot, "Engine", "Build", "CISCounter.txt");
		for(int Retry = 0; Retry < 20; Retry++)
		{
			int NowMinutes = (int)((DateTime.UtcNow - new DateTime(2014, 1, 1)).TotalMinutes);
			if (NowMinutes < 3 * 30 * 24)
			{
				throw new AutomationException("bad date calc");
			}
			if (!FileExists_NoExceptions(P4IndexFileLocal))
			{
				LogVerbose("{0} doesn't exist, checking in a new one", P4IndexFileP4);
				WriteAllText(P4IndexFileLocal, "-1 0");
				int WorkingCL = -1;
				try
				{
					WorkingCL = P4.CreateChange(P4Env.Client, "Adding new CIS Counter");
					P4.Add(WorkingCL, P4IndexFileP4);
					int SubmittedCL;
					P4.Submit(WorkingCL, out SubmittedCL);
				}
				catch (Exception)
				{
					LogWarning("Add of CIS counter failed, assuming it now exists.");
					if (WorkingCL > 0)
					{
						P4.DeleteChange(WorkingCL);
					}
				}
			}
			P4.Sync("-f " + P4IndexFileP4 + "#head");
			if (!FileExists_NoExceptions(P4IndexFileLocal))
			{
				LogVerbose("{0} doesn't exist, checking in a new one", P4IndexFileP4);
				WriteAllText(P4IndexFileLocal, "-1 0");
				int WorkingCL = -1;
				try
				{
					WorkingCL = P4.CreateChange(P4Env.Client, "Adding new CIS Counter");
					P4.Add(WorkingCL, P4IndexFileP4);
					int SubmittedCL;
					P4.Submit(WorkingCL, out SubmittedCL);
				}
				catch (Exception)
				{
					LogWarning("Add of CIS counter failed, assuming it now exists.");
					if (WorkingCL > 0)
					{
						P4.DeleteChange(WorkingCL);
					}
				}
			}
			string Data = ReadAllText(P4IndexFileLocal);
			string[] Parts = Data.Split(" ".ToCharArray());
			int Index = int.Parse(Parts[0]);
			int Minutes = int.Parse(Parts[1]);

			int DeltaMinutes = NowMinutes - Minutes;

			int TimeQuantum = 20;
			if (BranchOptions.QuantumOverride != 0)
			{
				TimeQuantum = BranchOptions.QuantumOverride;
			}
			int NewIndex = Index + 1;

			if (DeltaMinutes > TimeQuantum * 2)
			{
				if (DeltaMinutes > TimeQuantum * (1 << 8))
				{
					// it has been forever, lets just start over
					NewIndex = 0;
				}
				else
				{
					int WorkingIndex = NewIndex + 1;
					for (int WorkingDelta = DeltaMinutes - TimeQuantum; WorkingDelta > 0; WorkingDelta -= TimeQuantum, WorkingIndex++)
					{
						if (CountZeros(NewIndex) < CountZeros(WorkingIndex))
						{
							NewIndex = WorkingIndex;
						}
					}
				}
			}
			{
				string Line = String.Format("{0} {1}", NewIndex, NowMinutes);
				LogVerbose("Attempting to write {0} with {1}", P4IndexFileP4, Line);
				int WorkingCL = -1;
				try
				{
					WorkingCL = P4.CreateChange(P4Env.Client, "Updating CIS Counter");
					P4.Edit(WorkingCL, P4IndexFileP4);
					WriteAllText(P4IndexFileLocal, Line);
					int SubmittedCL;
					P4.Submit(WorkingCL, out SubmittedCL);
					return NewIndex;
				}
				catch (Exception)
				{
					LogWarning("Edit of CIS counter failed, assuming someone else checked in, retrying.");
					if (WorkingCL > 0)
					{
						P4.DeleteChange(WorkingCL);
					}
					System.Threading.Thread.Sleep(30000);
				}
			}
		}
		throw new AutomationException("Failed to update the CIS counter after 20 tries.");
	}

	private List<string> FindUnfinishedTriggers(bool bSkipTriggers, bool LocalOnly, string ExplicitTrigger, Dictionary<string, bool> GUBPNodesCompleted, List<string> OrdereredToDo)
	{
		// find all unfinished triggers, excepting the one we are triggering right now
		List<string> UnfinishedTriggers = new List<string>();
		if (!bSkipTriggers)
		{
			foreach (string NodeToDo in OrdereredToDo)
			{
				if (GUBPNodes[NodeToDo].Node.TriggerNode() && !NodeIsAlreadyComplete(GUBPNodesCompleted, NodeToDo, LocalOnly))
				{
					if (String.IsNullOrEmpty(ExplicitTrigger) || ExplicitTrigger != NodeToDo)
					{
						UnfinishedTriggers.Add(NodeToDo);
					}
				}
			}
		}
		return UnfinishedTriggers;
	}

	private void CheckSortOrder(bool LocalOnly, Dictionary<string, bool> GUBPNodesCompleted, List<string> OrdereredToDo)
	{
		foreach (string NodeToDo in OrdereredToDo)
		{
			if (GUBPNodes[NodeToDo].Node.TriggerNode() && (GUBPNodes[NodeToDo].Node.IsSticky() || NodeIsAlreadyComplete(GUBPNodesCompleted, NodeToDo, LocalOnly))) // these sticky triggers are ok, everything is already completed anyway
			{
				continue;
			}
			if (GUBPNodes[NodeToDo].Node.IsTest())
			{
				bHasTests = true;
			}
			int MyIndex = OrdereredToDo.IndexOf(NodeToDo);
			foreach (string Dep in GUBPNodes[NodeToDo].Node.FullNamesOfDependencies)
			{
				int DepIndex = OrdereredToDo.IndexOf(Dep);
				if (DepIndex >= MyIndex)
				{
					throw new AutomationException("Topological sort error, node {0} has a dependency of {1} which sorted after it.", NodeToDo, Dep);
				}
			}
			foreach (string Dep in GUBPNodes[NodeToDo].Node.FullNamesOfPseudosependencies)
			{
				int DepIndex = OrdereredToDo.IndexOf(Dep);
				if (DepIndex >= MyIndex)
				{
					throw new AutomationException("Topological sort error, node {0} has a pseduodependency of {1} which sorted after it.", NodeToDo, Dep);
				}
			}
		}
	}

	private void DoCommanderSetup(int TimeIndex, bool bSkipTriggers, bool bFakeEC, bool LocalOnly, string CLString, string ExplicitTrigger, Dictionary<string, string> GUBPNodesControllingTrigger, Dictionary<string, string> GUBPNodesControllingTriggerDotName, Dictionary<string, string> FullNodeList, Dictionary<string, string> FullNodeDirectDependencies, Dictionary<string, string> FullNodeDependedOnBy, Dictionary<string, string> FullNodeDependentPromotions, List<string> SeparatePromotables, Dictionary<string, int> FullNodeListSortKey, Dictionary<string, bool> GUBPNodesCompleted, Dictionary<string, NodeHistory> GUBPNodesHistory, List<string> OrdereredToDo, List<string> UnfinishedTriggers, string FakeFail)
	{

		if (OrdereredToDo.Count == 0)
		{
			throw new AutomationException("No nodes to do!");
		}
		List<string> ECProps = new List<string>();
		ECProps.Add(String.Format("TimeIndex={0}", TimeIndex));
		foreach (KeyValuePair<string, string> NodePair in FullNodeList)
		{
			ECProps.Add(string.Format("AllNodes/{0}={1}", NodePair.Key, NodePair.Value));
		}
		foreach (KeyValuePair<string, string> NodePair in FullNodeDirectDependencies)
		{
			ECProps.Add(string.Format("DirectDependencies/{0}={1}", NodePair.Key, NodePair.Value));
		}
		foreach (KeyValuePair<string, int> NodePair in FullNodeListSortKey)
		{
			ECProps.Add(string.Format("SortKey/{0}={1}", NodePair.Key, NodePair.Value));
		}
		foreach (KeyValuePair<string, string> NodePair in FullNodeDependedOnBy)
		{
			ECProps.Add(string.Format("DependedOnBy/{0}={1}", NodePair.Key, NodePair.Value));
		}
		foreach (KeyValuePair<string, string> NodePair in FullNodeDependentPromotions)
		{
			ECProps.Add(string.Format("DependentPromotions/{0}={1}", NodePair.Key, NodePair.Value));
		}
		foreach (string Node in SeparatePromotables)
		{
			ECProps.Add(string.Format("PossiblePromotables/{0}={1}", Node, ""));
		}
		List<string> ECJobProps = new List<string>();
		if (ExplicitTrigger != "")
		{
			ECJobProps.Add("IsRoot=0");
		}
		else
		{
			ECJobProps.Add("IsRoot=1");
		}

		List<string> FilteredOrdereredToDo = new List<string>();
		string StartFilterTime = DateTime.Now.ToString();
		// remove nodes that have unfinished triggers
		foreach (string NodeToDo in OrdereredToDo)
		{
			string ControllingTrigger = GetControllingTrigger(NodeToDo, GUBPNodesControllingTrigger);
			bool bNoUnfinishedTriggers = !UnfinishedTriggers.Contains(ControllingTrigger);

			if (bNoUnfinishedTriggers)
			{
				// if we are triggering, then remove nodes that are not controlled by the trigger or are dependencies of this trigger
				if (!String.IsNullOrEmpty(ExplicitTrigger))
				{
					if (ExplicitTrigger != NodeToDo && !NodeDependsOn(NodeToDo, ExplicitTrigger) && !NodeDependsOn(ExplicitTrigger, NodeToDo))
					{
						continue; // this wasn't on the chain related to the trigger we are triggering, so it is not relevant
					}
				}
				if (bPreflightBuild && !bSkipTriggers && GUBPNodes[NodeToDo].Node.TriggerNode())
				{
					// in preflight builds, we are either skipping triggers (and running things downstream) or we just stop at triggers and don't make them available for triggering.
					continue;
				}
				FilteredOrdereredToDo.Add(NodeToDo);
			}
		}

		OrdereredToDo = FilteredOrdereredToDo;
		string FinishFilterTime = DateTime.Now.ToString();
		PrintCSVFile(String.Format("UAT,FilterNodes,{0},{1}", StartFilterTime, FinishFilterTime));
		Log("*********** EC Nodes, in order.");
		PrintNodes(this, OrdereredToDo, GUBPNodesCompleted, GUBPNodesHistory, GUBPNodesControllingTriggerDotName, GUBPNodesControllingTrigger, LocalOnly, UnfinishedTriggers);
		string FinishNodePrint = DateTime.Now.ToString();
		PrintCSVFile(String.Format("UAT,SetupCommanderPrint,{0},{1}", FinishFilterTime, FinishNodePrint));
		// here we are just making sure everything before the explicit trigger is completed.
		if (!String.IsNullOrEmpty(ExplicitTrigger))
		{
			foreach (string NodeToDo in FilteredOrdereredToDo)
			{
				if (GUBPNodes[NodeToDo].Node.RunInEC() && !NodeIsAlreadyComplete(GUBPNodesCompleted, NodeToDo, LocalOnly) && NodeToDo != ExplicitTrigger && !NodeDependsOn(ExplicitTrigger, NodeToDo)) // if something is already finished, we don't put it into EC
				{
					throw new AutomationException("We are being asked to process node {0}, however, this is an explicit trigger {1}, so everything before it should already be handled. It seems likely that you waited too long to run the trigger. You will have to do a new build from scratch.", NodeToDo, ExplicitTrigger);
				}
			}
		}

		string LastSticky = "";
		bool HitNonSticky = false;
		bool bHaveECNodes = false;
		List<string> StepList = new List<string>();
		StepList.Add("use strict;");
		StepList.Add("use diagnostics;");
		StepList.Add("use ElectricCommander();");
		StepList.Add("my $ec = new ElectricCommander;");
		StepList.Add("$ec->setTimeout(600);");
		StepList.Add("my $batch = $ec->newBatch(\"serial\");");
		// sticky nodes are ones that we run on the main agent. We run then first and they must not be intermixed with parallel jobs
		foreach (string NodeToDo in OrdereredToDo)
		{
			if (GUBPNodes[NodeToDo].Node.RunInEC() && !NodeIsAlreadyComplete(GUBPNodesCompleted, NodeToDo, LocalOnly)) // if something is already finished, we don't put it into EC
			{
				bHaveECNodes = true;
				if (GUBPNodes[NodeToDo].Node.IsSticky())
				{
					LastSticky = NodeToDo;
					if (HitNonSticky && !bSkipTriggers)
					{
						throw new AutomationException("Sticky and non-sticky jobs did not sort right.");
					}
				}
				else
				{
					HitNonSticky = true;
				}
			}
		}

		string ParentPath = ParseParamValue("ParentPath");
		string StartPerlOutput = DateTime.Now.ToString();
		string BaseArgs = String.Format("$batch->createJobStep({{parentPath => '{0}'", ParentPath);

		bool bHasNoop = false;
		if (LastSticky == "" && bHaveECNodes)
		{
			// if we don't have any sticky nodes and we have other nodes, we run a fake noop just to release the resource 
			string Args = String.Format("{0}, subprocedure => 'GUBP_UAT_Node', parallel => '0', jobStepName => 'Noop', actualParameter => [{{actualParameterName => 'NodeName', value => 'Noop'}}, {{actualParameterName => 'Sticky', value =>'1' }}], releaseMode => 'release'}});", BaseArgs);
			StepList.Add(Args);
			bHasNoop = true;
		}

		List<string> FakeECArgs = new List<string>();
		Dictionary<string, List<string>> AgentGroupChains = new Dictionary<string, List<string>>();
		List<string> StickyChain = new List<string>();
		foreach (string NodeToDo in OrdereredToDo)
		{
			if (GUBPNodes[NodeToDo].Node.RunInEC() && !NodeIsAlreadyComplete(GUBPNodesCompleted, NodeToDo, LocalOnly)) // if something is already finished, we don't put it into EC  
			{
				string MyAgentGroup = GUBPNodes[NodeToDo].Node.AgentSharingGroup;
				if (MyAgentGroup != "")
				{
					if (!AgentGroupChains.ContainsKey(MyAgentGroup))
					{
						AgentGroupChains.Add(MyAgentGroup, new List<string> { NodeToDo });
					}
					else
					{
						AgentGroupChains[MyAgentGroup].Add(NodeToDo);
					}
				}
			}
			if (GUBPNodes[NodeToDo].Node.IsSticky())
			{
				if (!StickyChain.Contains(NodeToDo))
				{
					StickyChain.Add(NodeToDo);
				}
			}
		}
		foreach (string NodeToDo in OrdereredToDo)
		{
			if (GUBPNodes[NodeToDo].Node.RunInEC() && !NodeIsAlreadyComplete(GUBPNodesCompleted, NodeToDo, LocalOnly)) // if something is already finished, we don't put it into EC  
			{
				string EMails;
				List<string> NodeProps = GetECPropsForNode(NodeToDo, CLString, out EMails);
				ECProps.AddRange(NodeProps);

				bool Sticky = GUBPNodes[NodeToDo].Node.IsSticky();
				bool DoParallel = !Sticky;
				if (GUBPNodes[NodeToDo].Node.ECProcedure() == "GUBP_UAT_Node_Parallel_AgentShare_Editor")
				{
					DoParallel = true;
				}
				if (Sticky && GUBPNodes[NodeToDo].Node.ECAgentString() != "")
				{
					throw new AutomationException("Node {1} is sticky but has agent requirements.", NodeToDo);
				}
				string Procedure = GUBPNodes[NodeToDo].Node.ECProcedure();
				if (GUBPNodes[NodeToDo].Node.IsSticky() && NodeToDo == LastSticky)
				{
					Procedure = Procedure + "_Release";
				}
				string Args = String.Format("{0}, subprocedure => '{1}', parallel => '{2}', jobStepName => '{3}', actualParameter => [{{actualParameterName => 'NodeName', value =>'{4}'}}",
					BaseArgs, Procedure, DoParallel ? 1 : 0, NodeToDo, NodeToDo);
				string ProcedureParams = GUBPNodes[NodeToDo].Node.ECProcedureParams();
				if (!String.IsNullOrEmpty(ProcedureParams))
				{
					Args = Args + ProcedureParams;
				}

				if ((Procedure == "GUBP_UAT_Trigger" || Procedure == "GUBP_Hardcoded_Trigger") && !String.IsNullOrEmpty(EMails))
				{
					Args = Args + ", {actualParameterName => 'EmailsForTrigger', value => \'" + EMails + "\'}";
				}
				Args = Args + "]";
				string PreCondition = "";
				string RunCondition = "";
				List<string> UncompletedEcDeps = new List<string>();
				List<string> UncompletedCompletedDeps = new List<string>();
				List<string> PreConditionUncompletedEcDeps = new List<string>();
				string MyAgentGroup = GUBPNodes[NodeToDo].Node.AgentSharingGroup;
				bool bDoNestedJobstep = false;
				bool bDoFirstNestedJobstep = false;


				string NodeParentPath = ParentPath;
				string PreconditionParentPath;

				PreconditionParentPath = ParentPath;
				{
					List<string> EcDeps = GetECDependencies(NodeToDo);
					foreach (string Dep in EcDeps)
					{
						if (GUBPNodes[Dep].Node.RunInEC() && !NodeIsAlreadyComplete(GUBPNodesCompleted, Dep, LocalOnly) && OrdereredToDo.Contains(Dep)) // if something is already finished, we don't put it into EC
						{
							if (OrdereredToDo.IndexOf(Dep) > OrdereredToDo.IndexOf(NodeToDo))
							{
								throw new AutomationException("Topological sort error, node {0} has a dependency of {1} which sorted after it.", NodeToDo, Dep);
							}
							UncompletedEcDeps.Add(Dep);
						}
					}
				}

				foreach (string Dep in UncompletedEcDeps)
				{
					PreConditionUncompletedEcDeps.Add(Dep);
				}
				List<string> CompletedDeps = GetCompletedOnlyDependencies(NodeToDo);
				foreach (string Dep in CompletedDeps)
				{
					if (GUBPNodes[Dep].Node.RunInEC() && !NodeIsAlreadyComplete(GUBPNodesCompleted, Dep, LocalOnly) && OrdereredToDo.Contains(Dep)) // if something is already finished, we don't put it into EC
					{
						if (OrdereredToDo.IndexOf(Dep) > OrdereredToDo.IndexOf(NodeToDo))
						{
							throw new AutomationException("Topological sort error, node {0} has a dependency of {1} which sorted after it.", NodeToDo, Dep);
						}
						UncompletedCompletedDeps.Add(Dep);
					}
				}
				if (MyAgentGroup != "")
				{
					bDoNestedJobstep = true;
					NodeParentPath = ParentPath + "/jobSteps[" + MyAgentGroup + "]";

					PreConditionUncompletedEcDeps = new List<string>();

					List<string> MyChain = AgentGroupChains[MyAgentGroup];
					int MyIndex = MyChain.IndexOf(NodeToDo);
					if (MyIndex > 0)
					{
						PreConditionUncompletedEcDeps.Add(MyChain[MyIndex - 1]);
					}
					else
					{
						bDoFirstNestedJobstep = bDoNestedJobstep;
						// to avoid idle agents (and also EC doesn't actually reserve our agent!), we promote all dependencies to the first one
						foreach (string Chain in MyChain)
						{
							List<string> EcDeps = GetECDependencies(Chain);
							foreach (string Dep in EcDeps)
							{
								if (GUBPNodes[Dep].Node.RunInEC() && !NodeIsAlreadyComplete(GUBPNodesCompleted, Dep, LocalOnly) && OrdereredToDo.Contains(Dep)) // if something is already finished, we don't put it into EC
								{
									if (OrdereredToDo.IndexOf(Dep) > OrdereredToDo.IndexOf(Chain))
									{
										throw new AutomationException("Topological sort error, node {0} has a dependency of {1} which sorted after it.", Chain, Dep);
									}
									if (!MyChain.Contains(Dep) && !PreConditionUncompletedEcDeps.Contains(Dep))
									{
										PreConditionUncompletedEcDeps.Add(Dep);
									}
								}
							}
						}
					}
				}
				if (GUBPNodes[NodeToDo].Node.IsSticky())
				{
					List<string> MyChain = StickyChain;
					int MyIndex = MyChain.IndexOf(NodeToDo);
					if (MyIndex > 0)
					{
						if (!PreConditionUncompletedEcDeps.Contains(MyChain[MyIndex - 1]) && !NodeIsAlreadyComplete(GUBPNodesCompleted, MyChain[MyIndex - 1], LocalOnly))
						{
							PreConditionUncompletedEcDeps.Add(MyChain[MyIndex - 1]);
						}
					}
					else
					{
						List<string> EcDeps = GetECDependencies(NodeToDo);
						foreach (string Dep in EcDeps)
						{
							if (GUBPNodes[Dep].Node.RunInEC() && !NodeIsAlreadyComplete(GUBPNodesCompleted, Dep, LocalOnly) && OrdereredToDo.Contains(Dep)) // if something is already finished, we don't put it into EC
							{
								if (OrdereredToDo.IndexOf(Dep) > OrdereredToDo.IndexOf(NodeToDo))
								{
									throw new AutomationException("Topological sort error, node {0} has a dependency of {1} which sorted after it.", NodeToDo, Dep);
								}
								if (!MyChain.Contains(Dep) && !PreConditionUncompletedEcDeps.Contains(Dep))
								{
									PreConditionUncompletedEcDeps.Add(Dep);
								}
							}
						}
					}
				}
				if (bHasNoop && PreConditionUncompletedEcDeps.Count == 0)
				{
					PreConditionUncompletedEcDeps.Add("Noop");
				}
				if (PreConditionUncompletedEcDeps.Count > 0)
				{
					PreCondition = "\"\\$\" . \"[/javascript if(";
					// these run "parallel", but we add preconditions to serialize them
					int Index = 0;
					foreach (string Dep in PreConditionUncompletedEcDeps)
					{
						PreCondition = PreCondition + "getProperty('" + GetJobStep(PreconditionParentPath, Dep) + "/status\') == \'completed\'";
						Index++;
						if (Index != PreConditionUncompletedEcDeps.Count)
						{
							PreCondition = PreCondition + " && ";
						}
					}
					PreCondition = PreCondition + ") true;]\"";
				}
				if (UncompletedCompletedDeps.Count > 0)
				{
					PreCondition = "\"\\$\" . \"[/javascript if(";
					// these run "parallel", but we add preconditions to serialize them
					int Index = 0;
					foreach (string Dep in CompletedDeps)
					{
						if (GUBPNodes[Dep].Node.RunInEC())
						{
							PreCondition = PreCondition + "getProperty('" + GetJobStep(PreconditionParentPath, Dep) + "/status\') == \'completed\'";
							Index++;
							if (Index != CompletedDeps.Count)
							{
								PreCondition = PreCondition + " && ";
							}
						}
					}
					PreCondition = PreCondition + ") true;]\"";
				}

				if (UncompletedEcDeps.Count > 0)
				{
					RunCondition = "\"\\$\" . \"[/javascript if(";
					int Index = 0;
					foreach (string Dep in UncompletedEcDeps)
					{
						RunCondition = RunCondition + "((\'\\$\" . \"[" + GetJobStep(PreconditionParentPath, Dep) + "/outcome]\' == \'success\') || ";
						RunCondition = RunCondition + "(\'\\$\" . \"[" + GetJobStep(PreconditionParentPath, Dep) + "/outcome]\' == \'warning\'))";

						Index++;
						if (Index != UncompletedEcDeps.Count)
						{
							RunCondition = RunCondition + " && ";
						}
					}
					RunCondition = RunCondition + ")true; else false;]\"";
				}

				if (bDoNestedJobstep)
				{
					if (bDoFirstNestedJobstep)
					{
						{
							string NestArgs = String.Format("$batch->createJobStep({{parentPath => '{0}', jobStepName => '{1}', parallel => '1'",
								ParentPath, MyAgentGroup);
							if (!String.IsNullOrEmpty(PreCondition))
							{
								NestArgs = NestArgs + ", precondition => " + PreCondition;
							}
							NestArgs = NestArgs + "});";
							StepList.Add(NestArgs);
						}
						{
							string NestArgs = String.Format("$batch->createJobStep({{parentPath => '{0}/jobSteps[{1}]', jobStepName => '{2}_GetPool', subprocedure => 'GUBP{3}_AgentShare_GetPool', parallel => '1', actualParameter => [{{actualParameterName => 'AgentSharingGroup', value => '{4}'}}, {{actualParameterName => 'NodeName', value => '{5}'}}]",
								ParentPath, MyAgentGroup, MyAgentGroup, GUBPNodes[NodeToDo].Node.ECProcedureInfix(), MyAgentGroup, NodeToDo);
							if (!String.IsNullOrEmpty(PreCondition))
							{
								NestArgs = NestArgs + ", precondition => " + PreCondition;
							}
							NestArgs = NestArgs + "});";
							StepList.Add(NestArgs);
						}
						{
							string NestArgs = String.Format("$batch->createJobStep({{parentPath => '{0}/jobSteps[{1}]', jobStepName => '{2}_GetAgent', subprocedure => 'GUBP{3}_AgentShare_GetAgent', parallel => '1', exclusiveMode => 'call', resourceName => '{4}', actualParameter => [{{actualParameterName => 'AgentSharingGroup', value => '{5}'}}, {{actualParameterName => 'NodeName', value=> '{6}'}}]",
								ParentPath, MyAgentGroup, MyAgentGroup, GUBPNodes[NodeToDo].Node.ECProcedureInfix(),
								String.Format("$[/myJob/jobSteps[{0}]/ResourcePool]", MyAgentGroup),
								MyAgentGroup, NodeToDo);
							{
								NestArgs = NestArgs + ", precondition  => ";
								NestArgs = NestArgs + "\"\\$\" . \"[/javascript if(";
								NestArgs = NestArgs + "getProperty('" + PreconditionParentPath + "/jobSteps[" + MyAgentGroup + "]/jobSteps[" + MyAgentGroup + "_GetPool]/status') == 'completed'";
								NestArgs = NestArgs + ") true;]\"";
							}
							NestArgs = NestArgs + "});";
							StepList.Add(NestArgs);
						}
						{
							PreCondition = "\"\\$\" . \"[/javascript if(";
							PreCondition = PreCondition + "getProperty('" + PreconditionParentPath + "/jobSteps[" + MyAgentGroup + "]/jobSteps[" + MyAgentGroup + "_GetAgent]/status') == 'completed'";
							PreCondition = PreCondition + ") true;]\"";
						}
					}
					Args = Args.Replace(String.Format("parentPath => '{0}'", ParentPath), String.Format("parentPath => '{0}'", NodeParentPath));
					Args = Args.Replace("UAT_Node_Parallel_AgentShare", "UAT_Node_Parallel_AgentShare3");
				}

				if (!String.IsNullOrEmpty(PreCondition))
				{
					Args = Args + ", precondition => " + PreCondition;
				}
				if (!String.IsNullOrEmpty(RunCondition))
				{
					Args = Args + ", condition => " + RunCondition;
				}
#if false
                    // this doesn't work because it includes precondition time
                    if (GUBPNodes[NodeToDo].TimeoutInMinutes() > 0)
                    {
                        Args = Args + String.Format(" --timeLimitUnits minutes --timeLimit {0}", GUBPNodes[NodeToDo].TimeoutInMinutes());
                    }
#endif
				if (Sticky && NodeToDo == LastSticky)
				{
					Args = Args + ", releaseMode => 'release'";
				}
				Args = Args + "});";
				StepList.Add(Args);
				if (bFakeEC &&
					!UnfinishedTriggers.Contains(NodeToDo) &&
					(GUBPNodes[NodeToDo].Node.ECProcedure().StartsWith("GUBP_UAT_Node") || GUBPNodes[NodeToDo].Node.ECProcedure().StartsWith("GUBP_Mac_UAT_Node")) // other things we really can't test
					) // unfinished triggers are never run directly by EC, rather it does another job setup
				{
					string Arg = String.Format("gubp -Node={0} -FakeEC {1} {2} {3} {4} {5}",
						NodeToDo,
						bFake ? "-Fake" : "",
						ParseParam("AllPlatforms") ? "-AllPlatforms" : "",
						ParseParam("UnfinishedTriggersFirst") ? "-UnfinishedTriggersFirst" : "",
						ParseParam("UnfinishedTriggersParallel") ? "-UnfinishedTriggersParallel" : "",
						ParseParam("WithMac") ? "-WithMac" : ""
						);

					string Node = ParseParamValue("-Node");
					if (!String.IsNullOrEmpty(Node))
					{
						Arg = Arg + " -Node=" + Node;
					}
					if (!String.IsNullOrEmpty(FakeFail))
					{
						Arg = Arg + " -FakeFail=" + FakeFail;
					}
					FakeECArgs.Add(Arg);
				}

				if (MyAgentGroup != "" && !bDoNestedJobstep)
				{
					List<string> MyChain = AgentGroupChains[MyAgentGroup];
					int MyIndex = MyChain.IndexOf(NodeToDo);
					if (MyIndex == MyChain.Count - 1)
					{
						string RelPreCondition = "\"\\$\" . \"[/javascript if(";
						// this runs "parallel", but we a precondition to serialize it
						RelPreCondition = RelPreCondition + "getProperty('" + PreconditionParentPath + "/jobSteps[" + NodeToDo + "]/status') == 'completed'";
						RelPreCondition = RelPreCondition + ") true;]\"";
						// we need to release the resource
						string RelArgs = String.Format("{0}, subprocedure => 'GUBP_Release_AgentShare', parallel => '1', jobStepName => 'Release_{1}', actualParameter => [{{actualParameterName => 'AgentSharingGroup', valued => '{2}'}}], releaseMode => 'release', precondition => '{3}'",
							BaseArgs, MyAgentGroup, MyAgentGroup, RelPreCondition);
						StepList.Add(RelArgs);
					}
				}
			}
		}
		WriteECPerl(StepList);
		string FinishPerlOutput = DateTime.Now.ToString();
		PrintCSVFile(String.Format("UAT,PerlOutput,{0},{1}", StartPerlOutput, FinishPerlOutput));
		RunECTool(String.Format("setProperty \"/myWorkflow/HasTests\" \"{0}\"", bHasTests));
		{
			ECProps.Add("GUBP_LoadedProps=1");
			string BranchDefFile = CommandUtils.CombinePaths(CommandUtils.CmdEnv.LogFolder, "BranchDef.properties");
			CommandUtils.WriteAllLines(BranchDefFile, ECProps.ToArray());
			RunECTool(String.Format("setProperty \"/myWorkflow/BranchDefFile\" \"{0}\"", BranchDefFile.Replace("\\", "\\\\")));
		}
		{
			ECProps.Add("GUBP_LoadedJobProps=1");
			string BranchJobDefFile = CommandUtils.CombinePaths(CommandUtils.CmdEnv.LogFolder, "BranchJobDef.properties");
			CommandUtils.WriteAllLines(BranchJobDefFile, ECProps.ToArray());
			RunECTool(String.Format("setProperty \"/myJob/BranchJobDefFile\" \"{0}\"", BranchJobDefFile.Replace("\\", "\\\\")));
		}
		if (bFakeEC)
		{
			foreach (string Args in FakeECArgs)
			{
				RunUAT(CmdEnv, Args);
			}
		}
	}

	void ExecuteNodes(List<string> OrdereredToDo, bool bOnlyNode, bool bFakeEC, bool LocalOnly, bool bSaveSharedTempStorage, Dictionary<string, bool> GUBPNodesCompleted, Dictionary<string, NodeHistory> GUBPNodesHistory, string CLString, string FakeFail)
	{
        Dictionary<string, string> BuildProductToNodeMap = new Dictionary<string, string>();
		foreach (string NodeToDo in OrdereredToDo)
        {
            if (GUBPNodes[NodeToDo].Node.BuildProducts != null || GUBPNodes[NodeToDo].Node.AllDependencyBuildProducts != null)
            {
                throw new AutomationException("topological sort error");
            }

            GUBPNodes[NodeToDo].Node.AllDependencyBuildProducts = new List<string>();
            GUBPNodes[NodeToDo].Node.AllDependencies = new List<string>();
            foreach (string Dep in GUBPNodes[NodeToDo].Node.FullNamesOfDependencies)
            {
                GUBPNodes[NodeToDo].Node.AddAllDependent(Dep);
                if (GUBPNodes[Dep].Node.AllDependencies == null)
                {
                    if (!bOnlyNode)
                    {
                        throw new AutomationException("Node {0} was not processed yet3?  Processing {1}", Dep, NodeToDo);
                    }
                }
                else
				{
					foreach (string DepDep in GUBPNodes[Dep].Node.AllDependencies)
					{
						GUBPNodes[NodeToDo].Node.AddAllDependent(DepDep);
					}
				}
				
                if (GUBPNodes[Dep].Node.BuildProducts == null)
                {
                    if (!bOnlyNode)
                    {
                        throw new AutomationException("Node {0} was not processed yet? Processing {1}", Dep, NodeToDo);
                    }
                }
                else
                {
                    foreach (string Prod in GUBPNodes[Dep].Node.BuildProducts)
                    {
                        GUBPNodes[NodeToDo].Node.AddDependentBuildProduct(Prod);
                    }
                    if (GUBPNodes[Dep].Node.AllDependencyBuildProducts == null)
                    {
                        throw new AutomationException("Node {0} was not processed yet2?  Processing {1}", Dep, NodeToDo);
                    }
                    foreach (string Prod in GUBPNodes[Dep].Node.AllDependencyBuildProducts)
                    {
                        GUBPNodes[NodeToDo].Node.AddDependentBuildProduct(Prod);
                    }
                }
            }

            string NodeStoreName = StoreName + "-" + GUBPNodes[NodeToDo].Node.GetFullName();
            
            string GameNameIfAny = GUBPNodes[NodeToDo].Node.GameNameIfAnyForTempStorage();
            string StorageRootIfAny = GUBPNodes[NodeToDo].Node.RootIfAnyForTempStorage();
					
            if (bFake)
            {
                StorageRootIfAny = ""; // we don't rebase fake runs since those are entirely "records of success", which are always in the logs folder
            }

            // this is kinda complicated
            bool SaveSuccessRecords = (IsBuildMachine || bFakeEC) && // no real reason to make these locally except for fakeEC tests
                (!GUBPNodes[NodeToDo].Node.TriggerNode() || GUBPNodes[NodeToDo].Node.IsSticky()) // trigger nodes are run twice, one to start the new workflow and once when it is actually triggered, we will save reconds for the latter
                && (GUBPNodes[NodeToDo].Node.RunInEC() || !GUBPNodes[NodeToDo].Node.IsAggregate()); //aggregates not in EC can be "run" multiple times, so we can't track those

            Log("***** Running GUBP Node {0} -> {1} : {2}", GUBPNodes[NodeToDo].Node.GetFullName(), GameNameIfAny, NodeStoreName);
            if (NodeIsAlreadyComplete(GUBPNodesCompleted, NodeToDo, LocalOnly))
            {
                if (NodeToDo == VersionFilesNode.StaticGetFullName() && !IsBuildMachine)
                {
                    Log("***** NOT ****** Retrieving GUBP Node {0} from {1}; it is the version files.", GUBPNodes[NodeToDo].Node.GetFullName(), NodeStoreName);
                    GUBPNodes[NodeToDo].Node.BuildProducts = new List<string>();

                }
                else
                {
                    Log("***** Retrieving GUBP Node {0} from {1}", GUBPNodes[NodeToDo].Node.GetFullName(), NodeStoreName);
                    bool WasLocal;
					try
					{
						GUBPNodes[NodeToDo].Node.BuildProducts = TempStorage.RetrieveFromTempStorage(CmdEnv, NodeStoreName, out WasLocal, GameNameIfAny, StorageRootIfAny);
					}
					catch
					{
						if(GameNameIfAny != "")
						{
							GUBPNodes[NodeToDo].Node.BuildProducts = TempStorage.RetrieveFromTempStorage(CmdEnv, NodeStoreName, out WasLocal, "", StorageRootIfAny);
						}
						else
						{
							throw new AutomationException("Build Products cannot be found for node {0}", NodeToDo);
						}
					}
                    if (!WasLocal)
                    {
                        GUBPNodes[NodeToDo].Node.PostLoadFromSharedTempStorage(this);
                    }
                }
            }
            else
            {
                if (SaveSuccessRecords) 
                {
                    SaveStatus(NodeToDo, StartedTempStorageSuffix, NodeStoreName, bSaveSharedTempStorage, GameNameIfAny);
                }
				double BuildDuration = 0.0;
                try
                {
                    if (!String.IsNullOrEmpty(FakeFail) && FakeFail.Equals(NodeToDo, StringComparison.InvariantCultureIgnoreCase))
                    {
                        throw new AutomationException("Failing node {0} by request.", NodeToDo);
                    }
                    if (bFake)
                    {
                        Log("***** FAKE!! Building GUBP Node {0} for {1}", NodeToDo, NodeStoreName);
                        GUBPNodes[NodeToDo].Node.DoFakeBuild(this);
                    }
                    else
                    {                        
						Log("***** Building GUBP Node {0} for {1}", NodeToDo, NodeStoreName);
						DateTime StartTime = DateTime.UtcNow;
                        string StartBuild = DateTime.Now.ToString();
                        GUBPNodes[NodeToDo].Node.DoBuild(this);
                        string FinishBuild = DateTime.Now.ToString();
                        if (IsBuildMachine)
                        {
                            PrintCSVFile(String.Format("UAT,DoBuild.{0},{1},{2}", NodeToDo, StartBuild, FinishBuild));
                        }
						BuildDuration = (DateTime.UtcNow - StartTime).TotalMilliseconds / 1000;
						
                    }
                    if (!GUBPNodes[NodeToDo].Node.IsAggregate())
                    {
						double StoreDuration = 0.0;
						DateTime StartTime = DateTime.UtcNow;
                        string StartStore = DateTime.Now.ToString();
                        TempStorage.StoreToTempStorage(CmdEnv, NodeStoreName, GUBPNodes[NodeToDo].Node.BuildProducts, !bSaveSharedTempStorage, GameNameIfAny, StorageRootIfAny);
						StoreDuration = (DateTime.UtcNow - StartTime).TotalMilliseconds / 1000;
                        string FinishStore = DateTime.Now.ToString();                        
						Log("Took {0} seconds to store build products", StoreDuration);
						if (IsBuildMachine)
						{
                            PrintCSVFile(String.Format("UAT,StoreBuildProducts,{0},{1}", StartStore, FinishStore));
							RunECTool(String.Format("setProperty \"/myJobStep/StoreDuration\" \"{0}\"", StoreDuration.ToString()));
						}
                        if (ParseParam("StompCheck"))
                        {
                            foreach (string Dep in GUBPNodes[NodeToDo].Node.AllDependencies)
                            {
                                try
                                {
                                    bool WasLocal;
                                    string StartRetrieve = DateTime.Now.ToString();
									TempStorage.RetrieveFromTempStorage(CmdEnv, NodeStoreName, out WasLocal, GameNameIfAny, StorageRootIfAny);
                                    string FinishRetrieve = DateTime.Now.ToString();
                                    if (IsBuildMachine)
                                    {
                                        PrintCSVFile(String.Format("UAT,RetrieveBuildProducts,{0},{1}", StartRetrieve, FinishRetrieve));
                                    }
									if (!WasLocal)
									{
										throw new AutomationException("Retrieve was not local?");
									}
																	                                    
                                }
                                catch(Exception Ex)
                                {
                                    throw new AutomationException("Node {0} stomped Node {1}   Ex: {2}", NodeToDo, Dep, LogUtils.FormatException(Ex));
                                }
                            }
                        }

                    }
                }
                catch (Exception Ex)
                {
                    if (SaveSuccessRecords)
                    {
                        string StartNodeHistory = DateTime.Now.ToString();
                        UpdateNodeHistory(GUBPNodesHistory, NodeToDo, CLString);
                        string FinishNodeHistory = DateTime.Now.ToString();                        
                        SaveStatus(NodeToDo, FailedTempStorageSuffix, NodeStoreName, bSaveSharedTempStorage, GameNameIfAny, ParseParamValue("MyJobStepId"));
                        string FinishSaveStatus = DateTime.Now.ToString();                        
                        UpdateECProps(NodeToDo, CLString);
                        string FinishECPropUpdate = DateTime.Now.ToString();
                        
						if (IsBuildMachine)
						{
							GetFailureEmails(GUBPNodesHistory, NodeToDo, CLString);
                            string FinishFailEmails = DateTime.Now.ToString();
                            PrintCSVFile(String.Format("UAT,UpdateNodeHistory,{0},{1}", StartNodeHistory, FinishNodeHistory));
                            PrintCSVFile(String.Format("UAT,SaveNodeStatus,{0},{1}", FinishNodeHistory, FinishSaveStatus));
                            PrintCSVFile(String.Format("UAT,UpdateECProps,{0},{1}", FinishSaveStatus, FinishECPropUpdate));
                            PrintCSVFile(String.Format("UAT,GetFailEmails,{0},{1}", FinishECPropUpdate, FinishFailEmails));
						}
						UpdateECBuildTime(NodeToDo, BuildDuration);
                    }

                    Log("{0}", ExceptionToString(Ex));


                    if (GUBPNodesHistory.ContainsKey(NodeToDo))
                    {
                        NodeHistory History = GUBPNodesHistory[NodeToDo];
                        Log("Changes since last green *********************************");
                        Log("");
                        Log("");
                        Log("");
                        PrintDetailedChanges(History);
                        Log("End changes since last green");
                    }

                    string FailInfo = "";
                    FailInfo += "********************************* Main log file";
                    FailInfo += Environment.NewLine + Environment.NewLine;
                    FailInfo += LogUtils.GetLogTail();
                    FailInfo += Environment.NewLine + Environment.NewLine + Environment.NewLine;



                    string OtherLog = "See logfile for details: '";
                    if (FailInfo.Contains(OtherLog))
                    {
                        string LogFile = FailInfo.Substring(FailInfo.IndexOf(OtherLog) + OtherLog.Length);
                        if (LogFile.Contains("'"))
                        {
                            LogFile = CombinePaths(CmdEnv.LogFolder, LogFile.Substring(0, LogFile.IndexOf("'")));
                            if (FileExists_NoExceptions(LogFile))
                            {
                                FailInfo += "********************************* Sub log file " + LogFile;
                                FailInfo += Environment.NewLine + Environment.NewLine;

                                FailInfo += LogUtils.GetLogTail(LogFile);
                                FailInfo += Environment.NewLine + Environment.NewLine + Environment.NewLine;
                            }
                        }
                    }

                    string Filename = CombinePaths(CmdEnv.LogFolder, "LogTailsAndChanges.log");
                    WriteAllText(Filename, FailInfo);

                    throw(Ex);
                }
                if (SaveSuccessRecords) 
                {
                    string StartNodeHistory = DateTime.Now.ToString();
                    UpdateNodeHistory(GUBPNodesHistory, NodeToDo, CLString);
                    string FinishNodeHistory = DateTime.Now.ToString();
                    SaveStatus(NodeToDo, SucceededTempStorageSuffix, NodeStoreName, bSaveSharedTempStorage, GameNameIfAny);
                    string FinishSaveStatus = DateTime.Now.ToString();                    
                    UpdateECProps(NodeToDo, CLString);
                    string FinishECPropUpdate = DateTime.Now.ToString();
                    
					if (IsBuildMachine)
					{
						GetFailureEmails(GUBPNodesHistory, NodeToDo, CLString);
                        string FinishFailEmails = DateTime.Now.ToString();
                        PrintCSVFile(String.Format("UAT,UpdateNodeHistory,{0},{1}", StartNodeHistory, FinishNodeHistory));
                        PrintCSVFile(String.Format("UAT,SaveNodeStatus,{0},{1}", FinishNodeHistory, FinishSaveStatus));
                        PrintCSVFile(String.Format("UAT,UpdateECProps,{0},{1}", FinishSaveStatus, FinishECPropUpdate));
                        PrintCSVFile(String.Format("UAT,GetFailEmails,{0},{1}", FinishECPropUpdate, FinishFailEmails));
					}
					UpdateECBuildTime(NodeToDo, BuildDuration);
                }
            }
            foreach (string Product in GUBPNodes[NodeToDo].Node.BuildProducts)
            {
                if (BuildProductToNodeMap.ContainsKey(Product))
                {
                    throw new AutomationException("Overlapping build product: {0} and {1} both produce {2}", BuildProductToNodeMap[Product], NodeToDo, Product);
                }
                BuildProductToNodeMap.Add(Product, NodeToDo);
            }
        }        
        PrintRunTime();
    }
    
	/// <summary>
	/// Sorts a list of nodes to display in EC. The default order is based on execution order and agent groups, whereas this function arranges nodes by
	/// frequency then execution order, while trying to group nodes on parallel paths (eg. Mac/Windows editor nodes) together.
	/// </summary>
	static Dictionary<string, int> GetDisplayOrder(List<string> NodeNames, Dictionary<string, string> InitialNodeDependencyNames, Dictionary<string, NodeInfo> GUBPNodes)
	{
		// Split the nodes into separate lists for each frequency
		SortedDictionary<int, List<string>> NodesByFrequency = new SortedDictionary<int,List<string>>();
		foreach(string NodeName in NodeNames)
		{
			List<string> NodesByThisFrequency;
			if(!NodesByFrequency.TryGetValue(GUBPNodes[NodeName].Node.DependentCISFrequencyQuantumShift(), out NodesByThisFrequency))
			{
				NodesByThisFrequency = new List<string>();
				NodesByFrequency.Add(GUBPNodes[NodeName].Node.DependentCISFrequencyQuantumShift(), NodesByThisFrequency);
			}
			NodesByThisFrequency.Add(NodeName);
		}

		// Build the output list by scanning each frequency in order
		HashSet<string> VisitedNodes = new HashSet<string>();
		Dictionary<string, int> SortedNodes = new Dictionary<string,int>();
		foreach(List<string> NodesByThisFrequency in NodesByFrequency.Values)
		{
			// Find a list of nodes in each display group. If the group name matches the node name, put that node at the front of the list.
			Dictionary<string, string> DisplayGroups = new Dictionary<string,string>();
			foreach(string NodeName in NodesByThisFrequency)
			{
				string GroupName = GUBPNodes[NodeName].Node.GetDisplayGroupName();
				if(!DisplayGroups.ContainsKey(GroupName))
				{
					DisplayGroups.Add(GroupName, NodeName);
				}
				else if(GroupName == NodeName)
				{
					DisplayGroups[GroupName] = NodeName + " " + DisplayGroups[GroupName];
				}
				else
				{
					DisplayGroups[GroupName] = DisplayGroups[GroupName] + " " + NodeName;
				}
			}

			// Build a list of ordering dependencies, putting all Mac nodes after Windows nodes with the same names.
			Dictionary<string, string> NodeDependencyNames = new Dictionary<string,string>(InitialNodeDependencyNames);
			foreach(KeyValuePair<string, string> DisplayGroup in DisplayGroups)
			{
				string[] GroupNodes = DisplayGroup.Value.Split(' ');
				for(int Idx = 1; Idx < GroupNodes.Length; Idx++)
				{
					NodeDependencyNames[GroupNodes[Idx]] += " " + GroupNodes[0];
				}
			}

			// Add nodes for each frequency into the master list, trying to match up different groups along the way
			foreach(string FirstNodeName in NodesByThisFrequency)
			{
				string[] GroupNodeNames = DisplayGroups[GUBPNodes[FirstNodeName].Node.GetDisplayGroupName()].Split(' ');
				foreach(string GroupNodeName in GroupNodeNames)
				{
					AddNodeAndDependencies(GroupNodeName, NodeDependencyNames, VisitedNodes, SortedNodes);
				}
			}
		}
		return SortedNodes;
	}

	static void AddNodeAndDependencies(string NodeName, Dictionary<string, string> NodeDependencyNames, HashSet<string> VisitedNodes, Dictionary<string, int> SortedNodes)
	{
		if(!VisitedNodes.Contains(NodeName))
		{
			VisitedNodes.Add(NodeName);
			foreach(string NodeDependencyName in NodeDependencyNames[NodeName].Split(new char[]{ ' ' }, StringSplitOptions.RemoveEmptyEntries))
			{
				AddNodeAndDependencies(NodeDependencyName, NodeDependencyNames, VisitedNodes, SortedNodes);
			}
			SortedNodes.Add(NodeName, SortedNodes.Count);
		}
	}

    string StartedTempStorageSuffix = "_Started";
    string FailedTempStorageSuffix = "_Failed";
    string SucceededTempStorageSuffix = "_Succeeded";
}
