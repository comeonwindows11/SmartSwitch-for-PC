# Politique de sécurité

SmartSwitch manipule des fichiers personnels et ouvre un service temporaire sur
le réseau local. Toute vulnérabilité doit être traitée avec précaution.

## Signaler un problème

N'ouvrez pas d'issue publique pour une vulnérabilité exploitable. Contactez les
mainteneurs du dépôt par un canal privé de sécurité dès qu'il sera configuré.

Incluez :

- la version et le commit concernés;
- la configuration Windows;
- les étapes de reproduction minimales;
- l'impact estimé;
- un correctif proposé, si disponible.

N'incluez jamais de fichier migré réel, de code d'association actif, de journal
non nettoyé ou de donnée personnelle.

## Modèle de sécurité actuel

- TLS 1.2/1.3 protège le transport.
- Une preuve HMAC dérivée du code lie l'association au certificat TLS éphémère.
- PBKDF2-SHA-256 ralentit les essais de code.
- SHA-256 valide chaque fichier après transfert.
- Le receveur bloque les chemins absolus et les traversées de répertoires.
- Un receveur n'accepte qu'une connexion par session.

La version alpha ne doit pas être exposée directement à Internet. Utilisez-la
sur un LAN de confiance et générez un nouveau code pour chaque tentative.

## Données locales

Les journaux se trouvent sous `%LOCALAPPDATA%\SmartSwitch\Logs`. La
désinstallation conserve volontairement les journaux et les migrations reçues.
