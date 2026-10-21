.PHONY: run build clean

run:
	dotnet run --project server/Wharf.Server.csproj

build:
	dotnet build server/Wharf.Server.csproj

clean:
	dotnet clean server/Wharf.Server.csproj
	rm -rf server/bin server/obj
