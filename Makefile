.PHONY: run build publish clean

run:
	dotnet run --project src/Wharf/Wharf.csproj

build:
	dotnet build Wharf.slnx

publish:
	dotnet publish src/Wharf/Wharf.csproj -c Release -o publish

clean:
	dotnet clean Wharf.slnx
	rm -rf publish src/Wharf/bin src/Wharf/obj src/Wharf.Client/bin src/Wharf.Client/obj
