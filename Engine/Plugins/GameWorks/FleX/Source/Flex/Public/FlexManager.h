#pragma once

#include "GameWorks/IFlexPluginBridge.h"

#include "NvFlex.h"
#include "NvFlexExt.h"
#include "NvFlexDevice.h"


class FFlexManager : public IFlexPluginBridge
{
public:
	static FFlexManager& get()
	{
		static FFlexManager instance;
		return instance;
	}

	NvFlexLibrary* GetFlexLib() { return FlexLib; }


	FFlexManager();
	virtual ~FFlexManager() {}

	// IFlexPluginBridge impl.
	virtual void InitGamePhysPostRHI();
	virtual void TermGamePhys();

	virtual bool HasFlexAsset(class UStaticMesh* StaticMesh);

	virtual void ReImportFlexAsset(class UStaticMesh* StaticMesh);

	/** Retrive the container instance for a soft joint, will return a nullptr if it doesn't already exist */
	struct FFlexContainerInstance* FindFlexContainerInstance(class FPhysScene* PhysScene, class UFlexContainer* Template);

	/** Retrive the container instance for a template, will create the instance if it doesn't already exist */
	struct FFlexContainerInstance* FindOrCreateFlexContainerInstance(class FPhysScene* PhysScene, class UFlexContainer* Template);

	virtual void WaitFlexScenes(class FPhysScene* PhysScene);
	virtual void TickFlexScenes(class FPhysScene* PhysScene, ENamedThreads::Type CurrentThread, const FGraphEventRef& MyCompletionGraphEvent, float dt);
	virtual void CleanupFlexScenes(class FPhysScene* PhysScene);

	virtual void AddRadialForceToFlex(class FPhysScene* PhysScene, FVector Origin, float Radius, float Strength, ERadialImpulseFalloff Falloff);
	virtual void AddRadialImpulseToFlex(class FPhysScene* PhysScene, FVector Origin, float Radius, float Strength, ERadialImpulseFalloff Falloff, bool bVelChange);

	void StartFlexRecord(class FPhysScene* PhysScene);
	void StopFlexRecord(class FPhysScene* PhysScene);
	void ToggleFlexContainerDebugDraw(class UWorld* World);

	virtual void AttachFlexToComponent(class USceneComponent* Component, float Radius);


	virtual void CreateFlexEmitterInstance(struct FParticleEmitterInstance* EmitterInstance);
	virtual void DestroyFlexEmitterInstance(struct FParticleEmitterInstance* EmitterInstance);
	virtual void TickFlexEmitterInstance(struct FParticleEmitterInstance* EmitterInstance, float DeltaTime, bool bSuppressSpawning);
	virtual uint32 GetFlexEmitterInstanceRequiredBytes(struct FParticleEmitterInstance* EmitterInstance, uint32 uiBytes);
	virtual bool FlexEmitterInstanceSpawnParticle(struct FParticleEmitterInstance* EmitterInstance, struct FBaseParticle* Particle, uint32 CurrentParticleIndex);
	virtual void FlexEmitterInstanceKillParticle(struct FParticleEmitterInstance* EmitterInstance, int32 KillIndex);
	virtual bool IsFlexEmitterInstanceDynamicDataRequired(struct FParticleEmitterInstance* EmitterInstance);

	virtual class UObject* GetFirstFlexContainerTemplate(class UParticleSystemComponent* Component);

	virtual bool IsValidFlexEmitter(class UParticleEmitter* Emitter);

private:
	struct FPhysSceneContext;

	void TickFlexScenesTask(FFlexManager::FPhysSceneContext* PhysSceneContext, float dt, bool bIsLocked);

	static class UFlexAsset* GetFlexAsset(class UStaticMesh* StaticMesh);

private:
	bool bFlexInitialized;
	NvFlexLibrary* FlexLib;

	struct FPhysSceneContext
	{
		/** Map from Flex container template to instances belonging to this physscene */
		TMap<class UFlexContainer*, struct FFlexContainerInstance*> FlexContainerMap;
		FGraphEventRef FlexSimulateTaskRef;
	};
	TMap<class FPhysScene*, FPhysSceneContext> PhysSceneMap;

	mutable FRWLock PhysSceneMapLock;

};
