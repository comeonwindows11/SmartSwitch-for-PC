# Protocole réseau SmartSwitch v1

## Transport

- TCP, port par défaut `49736`;
- TLS 1.2 ou TLS 1.3;
- ALPN `smartswitch/1`;
- certificat serveur autosigné et temporaire, généré pour la session.

Le certificat n'est pas validé par une autorité publique. Son empreinte est
authentifiée par le protocole d'association décrit ci-dessous.

## Association

1. Le donneur ouvre TLS et envoie l'identifiant produit, la version de protocole,
   son nom de poste et un nonce aléatoire.
2. Le receveur retourne un sel, un défi et l'empreinte SHA-256 de son certificat.
3. Les deux côtés dérivent une clé avec PBKDF2-SHA-256, 200 000 itérations, à
   partir du code à 8 chiffres.
4. Le donneur envoie une preuve HMAC contenant son nonce, le défi et l'empreinte.
5. Le receveur vérifie puis retourne une preuve distincte.

Cette liaison empêche un intermédiaire ne connaissant pas le code de substituer
son propre certificat. Le code n'est jamais transmis.

## Trames

Les messages de contrôle sont sérialisés en JSON UTF-8 et précédés d'une
longueur entière 32 bits en ordre réseau. La taille maximale d'une trame est de
4 Mio.

Après le manifeste :

1. `FileHeader` annonce chemin relatif, taille et date;
2. exactement `Length` octets sont transmis;
3. `FileTrailer` annonce le SHA-256;
4. le receveur compare, renomme le fichier `.smartswitch-partial`, puis répond.

Le transfert s'arrête à la première erreur d'intégrité. Le receveur écrit dans
une arborescence datée dédiée au donneur.

## Validation

Le receveur refuse :

- les versions ou identifiants produit inconnus;
- une preuve d'association invalide;
- un manifeste vide, négatif ou excessif;
- un chemin absolu, contenant un lecteur ou sortant du dossier de session;
- un chemin reçu deux fois;
- une taille incohérente;
- une empreinte de fichier invalide;
- un bilan final différent du manifeste.

## Évolutions prévues

- négociation de capacités;
- reprise par blocs avec identifiants de session;
- compression optionnelle;
- signature d'export pour les environnements gérés;
- découverte locale sans saisie d'adresse;
- rotation et limitation temporelle explicite des codes.
