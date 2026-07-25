# Contribuer à SmartSwitch

Merci de contribuer à SmartSwitch. Le projet privilégie des changements petits,
testables et faciles à relire.

## Préparer l'environnement

1. Installez Visual Studio 2026 et la charge de travail Desktop .NET.
2. Clonez le dépôt.
3. Exécutez `dotnet restore SmartSwitch.sln`.
4. Exécutez `.\build\Build.ps1 -Configuration Debug`.

## Règles d'architecture

- `SmartSwitch.Core` ne référence ni WPF, ni registre, ni API Win32.
- Les nouveaux domaines de migration sont des modules `IMigrationModule`.
- Les accès système et réseau restent dans `SmartSwitch.Infrastructure`.
- Le code-behind WPF se limite à `InitializeComponent` et à la composition de
  la fenêtre; le comportement appartient aux ViewModels et services.
- Les opérations longues doivent être asynchrones et annulables.
- Toute donnée reçue d'un autre PC doit être validée avant accès au disque.
- Ne journalisez jamais le code d'association, une clé dérivée ou le contenu
  d'un fichier migré.

## Ajouter un module

1. Implémentez `IMigrationModule`.
2. Donnez-lui un identifiant stable, unique et en minuscules.
3. Déclarez explicitement ses dépendances et catégories.
4. Retournez les avertissements récupérables dans `ModuleScanResult`.
5. Ajoutez des tests sur le scan, les erreurs et les dépendances.

Les types concrets sont découverts automatiquement dans les assemblies passées
à `AddSmartSwitch`.

## Validation locale

Avant une proposition :

```powershell
dotnet format SmartSwitch.sln --verify-no-changes
.\build\Build.ps1 -Configuration Release
.\build\Build-Installer.ps1
```

Pour un changement réseau, ajoutez au minimum un test de boucle locale. Les
tests ne doivent pas ouvrir un écouteur public; utilisez
`ListenOnLoopbackOnly: true`.

## Style de commit

Utilisez un message court à l'impératif, par exemple :

```text
Ajoute la reprise des transferts interrompus
```

Une pull request doit expliquer le comportement utilisateur, les choix de
sécurité et les commandes de validation exécutées.
