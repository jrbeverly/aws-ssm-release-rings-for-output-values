.PHONY: test publish-worker

test:
	dotnet test ReleaseRingService.slnx

publish-worker:
	dotnet publish src/ReleaseRingService/ReleaseRingService.Worker -c Release -o src/ReleaseRingService/ReleaseRingService.Worker/bin/Release/net10.0/publish
