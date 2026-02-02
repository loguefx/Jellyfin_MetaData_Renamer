#!/bin/bash
# Bash script to generate manifest.json with checksum and timestamp
# Usage: ./generate-manifest.sh [path-to-dll] [source-url]

DLL_PATH="${1:-../Jellyfin.Plugin.Template/bin/Release/net9.0/Jellyfin.Plugin.Template.dll}"
SOURCE_URL="${2:-https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/Jellyfin.Plugin.Template.dll}"

echo "Generating manifest.json..."

# Check if DLL exists
if [ ! -f "$DLL_PATH" ]; then
    echo "Error: DLL not found at $DLL_PATH"
    echo "Please build the plugin first: dotnet build -c Release"
    exit 1
fi

# Calculate SHA256 checksum
echo "Calculating checksum..."
if command -v sha256sum &> /dev/null; then
    CHECKSUM=$(sha256sum "$DLL_PATH" | cut -d' ' -f1 | tr '[:upper:]' '[:lower:]')
elif command -v shasum &> /dev/null; then
    CHECKSUM=$(shasum -a 256 "$DLL_PATH" | cut -d' ' -f1 | tr '[:upper:]' '[:lower:]')
else
    echo "Error: sha256sum or shasum not found"
    exit 1
fi

# Get current timestamp
TIMESTAMP=$(date -u +"%Y-%m-%dT%H:%M:%S.%3NZ")

# Update manifest.json using jq if available, otherwise use sed
MANIFEST_PATH="$(dirname "$0")/manifest.json"

if command -v jq &> /dev/null; then
    # Use jq for proper JSON manipulation
    jq --arg checksum "$CHECKSUM" \
       --arg timestamp "$TIMESTAMP" \
       --arg sourceUrl "$SOURCE_URL" \
       '.Plugins[0].Checksum = $checksum | 
        .Plugins[0].Timestamp = $timestamp | 
        .Plugins[0].SourceUrl = $sourceUrl' \
       "$MANIFEST_PATH" > "${MANIFEST_PATH}.tmp" && mv "${MANIFEST_PATH}.tmp" "$MANIFEST_PATH"
else
    # Fallback to sed (less reliable but works)
    sed -i.bak "s/PLACEHOLDER_CHECKSUM/$CHECKSUM/g" "$MANIFEST_PATH"
    sed -i.bak "s/PLACEHOLDER_TIMESTAMP/$TIMESTAMP/g" "$MANIFEST_PATH"
    sed -i.bak "s|https://raw.githubusercontent.com/YourUsername/Jellyfin_Metadata_tool/main/repository/Jellyfin.Plugin.Template.dll|$SOURCE_URL|g" "$MANIFEST_PATH"
    rm -f "${MANIFEST_PATH}.bak"
fi

echo ""
echo "Manifest updated successfully!"
echo "Checksum: $CHECKSUM"
echo "Timestamp: $TIMESTAMP"
echo ""
echo "Next steps:"
echo "1. Update SourceUrl in manifest.json to point to your hosted DLL"
echo "2. Host manifest.json and DLL on a web server (GitHub, web server, etc.)"
echo "3. Add repository URL to Jellyfin: Dashboard > Plugins > Repositories"
