[English](TROUBLESHOOTING.md) | **Français**

# Dépannage

| Symptôme | Cause probable | Solution |
|---|---|---|
| Le double Ctrl ne fait rien | appuis trop lents ; autre touche entre les deux appuis ; hook désactivé | augmenter `DoubleTapMs` ; vérifier l'icône de notification ; définir un raccourci alternatif (`Alt+Space`). Le hook est vérifié toutes les 30 s et réinstallé si Windows l'a retiré. |
| Le double Ctrl ne marche pas dans une application lancée en administrateur | isolation UIPI : un processus non élevé ne reçoit pas les touches des fenêtres élevées | comportement de Windows ; utiliser le raccourci depuis une autre fenêtre |
| « Le raccourci … est déjà utilisé » | une autre application a réservé la combinaison | en choisir une autre |
| Un fichier récent n'apparaît pas | indexation en cours, dossier exclu, fichier caché ou système, racine non surveillée | tableau de bord → files ; vérifier les exclusions ; « Réconcilier maintenant » |
| Le contenu d'un document n'est pas trouvé | indexation du contenu pas encore faite, taille supérieure à `MaxFileSizeMB`, fichier chiffré, PDF image (sans OCR), fichier cloud non téléchargé | tableau de bord → contenu et erreurs ; menu d'actions → Réindexer |
| Des fichiers semblent ne jamais être indexés (« documents indexés » bien inférieur au nombre de fichiers, file vide) | ces fichiers ne sont pas éligibles à l'indexation du contenu : format non pris en charge (images, exécutables…), dossier indexé par nom uniquement (Téléchargements), fichier vide ou trop volumineux, type exclu, fichier cloud… Ils restent trouvables par leur nom | tableau de bord › Index du contenu : la ligne « Fichiers » répartit **tous** les fichiers (indexés, sans texte, échecs, en attente, non éligibles) ; dépliez « Non éligibles » pour voir chaque raison et les extensions les plus fréquentes, puis ajustez Paramètres › Contenu si besoin. Dans le launcher, la pastille ○ d'un fichier donne aussi sa raison au survol |
| Fichiers OneDrive « en ligne uniquement » sans contenu | par conception, Seekbeam ne déclenche jamais leur téléchargement (lecture = modification du placeholder) | les rendre « toujours disponibles sur cet appareil » |
| Erreurs « Access denied » | dossiers protégés par les permissions Windows | normal : Seekbeam respecte les ACL et ne les contourne jamais |
| Indexation suspendue | mode Auto : plein écran, batterie, CPU ou mémoire saturés | tableau de bord → ressources ; changer de mode |
| Base corrompue (coupure de courant) | rare (SQLite WAL) | Seekbeam vérifie la base au démarrage après un arrêt anormal et reconstruit le Name Index depuis le catalogue. Sinon : Seekbeam fermé, supprimer `db\` et `index\` dans le dossier de données, puis relancer (réindexation complète). |
| Mémoire élevée | index en cache de pages (récupérable par Windows), extraction de gros documents | la RAM privée reste bornée ; l'hôte d'extraction est plafonné par un Job Object |
| Le contenu des fichiers de Téléchargements n'est pas trouvé | voulu : Téléchargements est indexé par nom uniquement ([SECURITY.fr.md](../SECURITY.fr.md)) | Paramètres › Contenu › retirer Téléchargements des « Dossiers indexés par nom uniquement » (lire d'abord l'avertissement) |
| Le tableau de bord indique « Isolation de l'extracteur : intégrité faible » | le bac à sable AppContainer n'a pas pu être mis en place : le dossier du programme Seekbeam est dans un emplacement indexé (cas typique d'une copie portable décompressée dans Documents), ses permissions ne peuvent pas être modifiées, ou `%LOCALAPPDATA%\Packages` est indexé. Le journal (lignes `Sandbox`, `Extractor`) donne la raison | déplacer le dossier portable hors des emplacements indexés, ou l'exclure ; ou accorder vous-même la lecture une fois : `icacls "<dossier de Seekbeam>" /grant *S-1-15-2-1:(OI)(CI)(RX)`, puis redémarrer Seekbeam. L'extraction fonctionne quand même en intégrité faible, avec une isolation plus faible |
| Le tableau de bord indique « Isolation de l'extracteur : aucune » | les deux bacs à sable ont échoué (inhabituel : logiciel de sécurité, stratégie) | voir le journal ; `Content.Sandbox` permet de forcer un niveau pour tester |

## Journaux et diagnostic
- Journaux : `%LOCALAPPDATA%\Seekbeam\logs\seekbeam-AAAAMMJJ.log` (tableau de bord → Ouvrir les journaux). Niveau réglable (`Logging.Level = Debug`).
- Ils ne contiennent ni recherches ni contenu de documents. Les chemins n'y figurent que dans les avertissements et les erreurs, et l'option `RedactPaths` les masque.
- Erreurs persistantes (2 000 dernières) : tableau de bord → Erreurs récentes.

## Réinitialiser
- Index seulement : tableau de bord → Réindexer les noms / le contenu.
- Tout : `Seekbeam.exe --purge-data` (supprime index, historique, journaux et configuration ; vos fichiers ne sont pas concernés).
