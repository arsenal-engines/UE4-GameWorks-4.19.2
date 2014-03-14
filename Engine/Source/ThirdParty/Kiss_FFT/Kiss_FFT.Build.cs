﻿// Copyright 1998-2014 Epic Games, Inc. All Rights Reserved.

using UnrealBuildTool;

public class Kiss_FFT : ModuleRules
{
	public Kiss_FFT(TargetInfo Target)
	{
		Type = ModuleType.External;

		Definitions.Add("WITH_KISSFFT=1");

		// Compile and link with kissFFT
		string Kiss_FFTPath = UEBuildConfiguration.UEThirdPartyDirectory + "Kiss_FFT/kiss_fft129";

		PublicIncludePaths.Add(Kiss_FFTPath);

		if (Target.Platform == UnrealTargetPlatform.Win64)
		{
			if (Target.Configuration == UnrealTargetConfiguration.Debug && BuildConfiguration.bDebugBuildsActuallyUseDebugCRT)
			{
				PublicLibraryPaths.Add(Kiss_FFTPath + "/lib/x64/Debug");
			}
			else
			{
				PublicLibraryPaths.Add(Kiss_FFTPath + "/lib/x64/Release");
			}

			PublicAdditionalLibraries.Add("KissFFT.lib");
		}
		else if (Target.Platform == UnrealTargetPlatform.Mac)
		{
			if (Target.Configuration == UnrealTargetConfiguration.Debug && BuildConfiguration.bDebugBuildsActuallyUseDebugCRT)
			{
				PublicAdditionalLibraries.Add(Kiss_FFTPath + "/Lib/Mac/Debug/libKissFFT.a");
			}
			else
			{
				PublicAdditionalLibraries.Add(Kiss_FFTPath + "/Lib/Mac/Release/libKissFFT.a");
			}
		}
	}
}
