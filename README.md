# SmartSwitch Migration Tool

SmartSwitch est une application Windows de migration de PC à PC écrite en C#,
.NET 10 et WPF. Le premier jalon livre un transfert réseau réel des fichiers du
profil utilisateur, une association par code et un assistant d'installation
autonome.

> État du projet : **alpha 0.1**. Le transfert de fichiers standard est
> fonctionnel. Les applications, paramètres Windows, profils complets et
> scénarios WinPE font partie de la feuille de route.

## Fonctionnalités disponibles

- page de démarrage avec les rôles **PC donneur** et **PC receveur**;
- modes Safe, Advanced et Custom;
- scan en lecture seule des dossiers Bureau, Documents, Téléchargements,
  Images, Musique et Vidéos;
- ajout d'un dossier personnalisé en mode Custom;
- association par code temporaire à 8 chiffres;
- canal TLS 1.2/1.3 avec preuve cryptographique liée au certificat de session;
- transfert en flux, sans charger les fichiers complets en mémoire;
- contrôle SHA-256 de chaque fichier avant validation;
- protection contre les chemins de sortie (`..`) côté receveur;
- progression et journal JSON Lines en temps réel;
- découverte automatique des modules `IMigrationModule` par injection de
  dépendances;
- assistant Setup WPF autonome, installant l'application pour l'utilisateur
  courant.

## Prérequis de développement

- Windows 11 ou Windows 10 récent;
- Visual Studio 2026 avec la charge de travail **Développement Desktop .NET**;
- SDK .NET 10.0.301 ou une version feature-band compatible;
- PowerShell 7 ou Windows PowerShell 5.1 pour les scripts de build.

Le fichier `global.json` sélectionne le SDK attendu. Aucun outil d'installation
tiers n'est nécessaire.

## Démarrage rapide

Ouvrez `SmartSwitch.sln` dans Visual Studio 2026, choisissez
`SmartSwitch.App` comme projet de démarrage, puis lancez avec `F5`.

En ligne de commande :

```powershell
dotnet restore SmartSwitch.sln
dotnet build SmartSwitch.sln --configuration Debug
dotnet test tests/SmartSwitch.Core.Tests/SmartSwitch.Core.Tests.csproj
dotnet run --project src/SmartSwitch.App/SmartSwitch.App.csproj
```

Le script suivant compile toute la solution et exécute les tests :

```powershell
.\build\Build.ps1 -Configuration Release
```

## Reconstruire l'installateur

Une seule commande publie l'application autonome, crée le payload compressé,
puis produit le Setup Wizard WPF en un fichier :

```powershell
.\build\Build-Installer.ps1
```

Résultat :

```text
artifacts\installer\SmartSwitch-Setup.exe
```

L'assistant :

- installe par défaut dans
  `%LOCALAPPDATA%\Programs\SmartSwitch Migration Tool`;
- crée un raccourci dans le menu Démarrer;
- peut créer un raccourci Bureau;
- inscrit une entrée de désinstallation Windows;
- inclut le runtime .NET 10, donc le poste cible n'a rien d'autre à installer.

Pour accélérer un rebuild déjà validé :

```powershell
.\build\Build-Installer.ps1 -SkipTests
```

## Effectuer une migration standard

Sur le PC receveur :

1. installez et ouvrez SmartSwitch;
2. choisissez **Recevoir sur ce PC**;
3. choisissez le dossier de destination;
4. cliquez sur **Démarrer la réception**;
5. notez l'adresse IP et le code affichés.

Sur le PC donneur :

1. installez et ouvrez la même version de SmartSwitch;
2. choisissez **Transférer depuis ce PC**;
3. gardez le mode Advanced et sélectionnez les dossiers;
4. entrez l'adresse et le code du receveur;
5. cliquez sur **Démarrer le transfert**.

Le receveur écoute sur le port TCP `49736`. Au premier usage, Windows peut
demander d'autoriser SmartSwitch sur le réseau privé. Les deux PC doivent être
joignables directement; la version actuelle n'utilise aucun relais Internet.

## Modes

| Mode | Comportement actuel |
| --- | --- |
| Safe | Scan en lecture seule, sans connexion ni écriture. |
| Advanced | Scan des catégories sélectionnées et transfert réseau standard. |
| Custom | Advanced avec ajout d'un dossier personnalisé. |

## Architecture

```text
src/
  SmartSwitch.Core/            Contrats, modèles et MigrationEngine
  SmartSwitch.Infrastructure/  Modules, réseau, système et journalisation
  SmartSwitch.App/             Application WPF MVVM
  SmartSwitch.Setup/           Assistant d'installation WPF autonome
tests/
  SmartSwitch.Core.Tests/      Tests Core et transfert réseau en boucle locale
build/
  Build.ps1
  Build-Installer.ps1
docs/
  ARCHITECTURE.md
  PROTOCOL.md
```

`SmartSwitch.Core` ne dépend ni de WPF ni des détails réseau. Les modules
implémentent `IMigrationModule`; l'infrastructure les découvre dans les
assemblies fournies et les enregistre auprès du conteneur DI. Le moteur vérifie
les identifiants, ordonne les dépendances et agrège les résultats de scan.

Des détails supplémentaires se trouvent dans
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) et
[docs/PROTOCOL.md](docs/PROTOCOL.md).

## Journaux

Les journaux structurés sont écrits ici :

```text
%LOCALAPPDATA%\SmartSwitch\Logs\smartswitch-AAAA-MM-JJ.jsonl
```

Ils peuvent contenir des noms de fichiers et de postes. Retirez les informations
sensibles avant de les joindre à un rapport public.

## Limites connues

- un seul donneur et un seul receveur par session;
- pas encore de reprise après interruption;
- pas encore de migration d'applications ou de paramètres;
- pas encore de mappage automatique des comptes utilisateurs;
- pas encore de signature de paquet ou de déploiement WinPE;
- l'association est destinée à un réseau local de confiance, pas à une
  exposition directe sur Internet.

## Contribution et sécurité

Consultez [CONTRIBUTING.md](CONTRIBUTING.md) avant une contribution. Signalez
les vulnérabilités selon [SECURITY.md](SECURITY.md), sans publier de secret ni
de données migrées dans une issue.

## Licence

SmartSwitch est distribué sous licence MIT. Voir [LICENSE](LICENSE).
