# Dossier de mises à jour

Déposer ici le fichier `agent.zip` généré par le script `make-update`.

Le serveur calculera automatiquement le hash SHA-256 à chaque appel de `/updates/check`.

## Générer le package

```powershell
.\Manage-AgentService.ps1 -Action make-update
```

Le ZIP est copié automatiquement dans ce dossier.
