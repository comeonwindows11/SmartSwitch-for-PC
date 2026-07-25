# Architecture

## Principes

SmartSwitch suit une séparation Core / Infrastructure / UI :

- **Core** décrit le métier sans dépendance Windows;
- **Infrastructure** adapte le système de fichiers, le réseau, les certificats,
  le registre et la journalisation;
- **App** fournit l'expérience WPF MVVM;
- **Setup** est un assistant WPF autonome, indépendant de l'application.

## Pipeline du moteur

```mermaid
flowchart LR
    UI["ViewModel donneur"] --> Request["MigrationRequest"]
    Request --> Engine["MigrationEngine"]
    Engine --> Catalog["Modules IMigrationModule"]
    Catalog --> Scan["ModuleScanResult"]
    Scan --> Summary["MigrationScanSummary"]
    Summary --> Network["NetworkTransferService"]
    Network --> Receiver["PC receveur"]
```

`MigrationEngine` résout les dépendances par parcours en profondeur, bloque les
cycles, exécute les modules dans l'ordre puis agrège fichiers, métadonnées et
avertissements.

## Découverte des modules

`ServiceCollectionExtensions.AddSmartSwitch` inspecte l'assembly
Infrastructure et les assemblies supplémentaires. Chaque classe concrète
implémentant `IMigrationModule` est enregistrée dans le conteneur DI. Le moteur
refuse les identifiants en double.

La première implémentation, `UserFilesMigrationModule`, énumère les dossiers
connus de l'utilisateur, évite les répertoires de jonction et conserve les
erreurs d'accès sous forme d'avertissements.

## UI

`ShellViewModel` assure la navigation entre :

- `LandingViewModel`;
- `DonorViewModel`;
- `ReceiverViewModel`.

Les vues utilisent des DataTemplates. Les dialogues de dossier passent par un
service afin de garder les ViewModels testables. Les services Core et
Infrastructure sont des singletons injectés au démarrage.

## Setup

Le script publie `SmartSwitch.App` en autonome, compresse le résultat et
l'embarque comme ressource dans `SmartSwitch.Setup`. L'assistant extrait le
payload dans un dossier temporaire protégé contre la traversée de chemins,
installe par utilisateur, crée les raccourcis et écrit un manifeste exact pour
la désinstallation.

## Extension prévue

Le prochain contrat de module pourra compléter `ScanAsync` avec des phases
Export/Import et un magasin de blobs commun. Les modèles actuels gardent déjà
les identifiants de module, chemins relatifs, tailles et métadonnées nécessaires
à cette évolution.
