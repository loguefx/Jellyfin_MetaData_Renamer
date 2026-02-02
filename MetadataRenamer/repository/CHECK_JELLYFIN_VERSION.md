# Important: Check Your Jellyfin Version!

The plugin manifest uses `TargetAbi: "10.9.0.0"`. **This must match your Jellyfin server version exactly.**

## How to Check Your Jellyfin Version

1. Open Jellyfin web UI
2. Go to **Dashboard** > **General** > **About**
3. Look for the version number (e.g., "10.9.13" or "10.10.0")

## Update TargetAbi if Needed

If your Jellyfin version is different:

1. **Jellyfin 10.8.x** → Change to `"TargetAbi": "10.8.0.0"`
2. **Jellyfin 10.9.x** → Keep `"TargetAbi": "10.9.0.0"` ✅ (Current)
3. **Jellyfin 10.10.x** → Change to `"TargetAbi": "10.10.0.0"`

## How to Update

1. Edit `manifest.json`
2. Change the `TargetAbi` value
3. Commit and push:
   ```powershell
   git add repository/manifest.json
   git commit -m "Update TargetAbi for Jellyfin X.X.X"
   git push
   ```
4. Wait 2-3 minutes for GitHub to update
5. Remove and re-add the repository in Jellyfin

## Why This Matters

Jellyfin will **ignore plugins** where the TargetAbi doesn't match the server version. This is a safety feature to prevent incompatible plugins from being installed.
