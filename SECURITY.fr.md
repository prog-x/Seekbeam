[English](SECURITY.md) | **Français**

# Politique de sécurité

## Versions prises en charge

Seule la dernière version publiée reçoit des correctifs. Vérifiez que le problème se produit toujours avec elle.

## Signaler une vulnérabilité

**N'ouvrez pas d'issue publique.** Signalez-la en privé via GitHub : sur la page du dépôt, ouvrez **Security › Report a vulnerability** ([lien direct](https://github.com/prog-x/Seekbeam/security/advisories/new)). Les signalements peuvent être rédigés en français ou en anglais.

Merci d'indiquer :
- la version et l'édition de Seekbeam (MSI, portable, compilé depuis les sources) et la version de Windows ;
- ce qu'un attaquant peut faire, et les étapes pour reproduire (si possible un fichier ou une arborescence minimale) ;
- si le problème est déjà public.

À quoi vous attendre : un accusé de réception sous 7 jours, une évaluation sous 30 jours, et un correctif publié au plus vite selon la gravité. Vous serez cité dans les notes de version, sauf si vous préférez le contraire. Seekbeam est maintenu bénévolement : ce sont des objectifs, pas des engagements contractuels.

## Ce que nous traitons comme un problème de sécurité

Seekbeam fait une promesse centrale : **il ne modifie, ne déplace, ne renomme ni ne supprime jamais les fichiers et dossiers qu'il indexe** ([docs/FILE_IMMUTABILITY_POLICY.fr.md](docs/FILE_IMMUTABILITY_POLICY.fr.md)). Sont traités comme des problèmes de sécurité :

- **Toute modification d'un fichier ou d'un dossier indexé causée par Seekbeam** : contenu, horodatages (hors heure du dernier accès mise à jour par Windows lui-même), attributs, permissions, ou fichier créé dans un dossier indexé. Pour le reproduire, l'outil d'empreintes aide : `dotnet run --project tools/Seekbeam.DatasetGen -- snapshot <dossier> avant.json`, reproduire, prendre `apres.json` de la même façon, puis `compare avant.json apres.json`.
- Un document ou une archive piégés qui font planter le processus d'extraction de façon exploitable, contournent ses limites (mémoire, CPU, durée, protection contre les bombes ZIP) ou écrivent sur le disque.
- Une sortie du bac à sable de l'extracteur : un document qui amène `Seekbeam.ExtractorHost.exe` à lire un autre fichier que celui en cours d'extraction, à accéder au réseau, à écrire ou à lancer un processus.
- Une écriture de Seekbeam hors de son dossier de données (`%LOCALAPPDATA%\Seekbeam`, ou `Data\` à côté de `Seekbeam.exe` en mode portable), par exemple via une jonction ou un chemin piégé. La seule exception est la mise en place unique du bac à sable (un profil AppContainer et une permission de lecture sur le dossier du programme Seekbeam, TD-17), jamais effectuée dans un dossier indexé.
- L'envoi hors de l'ordinateur du contenu de l'index, de chemins de fichiers ou de l'historique de recherche. Seekbeam n'a aucune télémétrie et ne fait aucune requête réseau.
- Une élévation de privilèges, ou la lecture de fichiers que l'utilisateur Windows courant ne peut pas lire.

## Comment Seekbeam limite l'impact d'un document malveillant

Indexer le contenu, c'est analyser automatiquement des documents, y compris des fichiers que vous n'avez jamais ouverts. Seekbeam part du principe qu'un document peut être hostile :

- **Téléchargements est indexé par nom uniquement.** Les fichiers venus d'Internet et les pièces jointes d'e-mails y arrivent : par défaut, leur contenu n'est jamais lu. Vous pouvez changer ce réglage dans **Paramètres › Contenu** ; Seekbeam explique d'abord le risque et demande une confirmation.
- **L'extraction s'exécute dans un bac à sable.** Le texte est extrait par un processus séparé, `Seekbeam.ExtractorHost.exe`, lancé dans un **AppContainer** Windows sans aucune capacité : il ne peut ni ouvrir vos fichiers, ni écrire, ni utiliser le réseau, ni lire le registre de l'utilisateur, ni lancer un autre processus. Seekbeam ouvre lui-même chaque document, en lecture seule, et transmet au bac à sable un handle qui ne permet que de lire ce seul fichier. Le processus est aussi borné par un Job Object (mémoire, CPU, durée) et s'exécute avec des protections contre l'exploitation. Si l'AppContainer est inutilisable, il se replie sur un processus en intégrité faible, qui ne peut pas écrire mais peut encore lire et utiliser le réseau. Le niveau utilisé est affiché dans le tableau de bord.
- **Limites par document** : taille, nombre de caractères, profondeur des archives, nombre d'entrées et taux de compression (bombes ZIP), et un délai au-delà duquel le processus d'extraction est tué puis relancé.

Les détails et les résultats des tests sont dans [TECHNICAL_DECISIONS.fr.md](TECHNICAL_DECISIONS.fr.md) (TD-17, TD-18).

## Tenir les dépendances à jour (mainteneurs)

Les analyseurs de documents sont le code le plus exposé : leurs mises à jour ne doivent pas attendre.

1. Dans les paramètres du dépôt (**Settings › Code security**), activer **Dependency graph**, **Dependabot alerts**, **Dependabot security updates** et **Private vulnerability reporting**. Les mises à jour de sécurité sont proposées dès qu'un avis est publié.
2. [`.github/dependabot.yml`](.github/dependabot.yml) propose aussi, chaque lundi, des mises à jour de version groupées : paquets NuGet (analyseurs dans leur propre groupe `document-parsers`), SDK .NET, qui fournit le runtime embarqué dans les versions publiées, et GitHub Actions (mensuel).
3. Chaque restauration exécute l'**audit NuGet** sur les paquets directs et transitifs. La CI traite une vulnérabilité connue comme une erreur : une dépendance vulnérable ne peut être ni fusionnée ni publiée. En local, `dotnet list package --vulnerable --include-transitive` donne le même rapport.
4. Ne fusionner une pull request de dépendance que si la CI est verte. La suite de tests comprend l'extraction de tous les formats, les tests de confinement du bac à sable et les tests de non-modification des fichiers. Publier ensuite une version : les utilisateurs ne reçoivent le correctif qu'avec une nouvelle version.

## Hors périmètre

- **Les modifications faites par une autre application** que Seekbeam a demandé à Windows d'ouvrir (Word, un éditeur, la fenêtre Propriétés). Ouvrir un fichier le confie à l'application associée, que Seekbeam ne contrôle pas.
- L'heure du dernier accès que NTFS peut mettre à jour à la lecture d'un fichier (comportement du système, voir le rapport d'audit).
- Les informations visibles par l'utilisateur propriétaire de l'index : l'index contient les noms, chemins et textes extraits de fichiers que cet utilisateur peut déjà lire.

Voir [docs/FILE_IMMUTABILITY_AUDIT.fr.md](docs/FILE_IMMUTABILITY_AUDIT.fr.md) pour la façon dont la garantie de non-modification est vérifiée, et pour ses limites.
