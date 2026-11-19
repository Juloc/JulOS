#!/bin/bash
printf '\033[0;30;47m'; clear
echo "black on white: The quick brown fox jumps over 0123456789 {}[]()"
printf '\033[31mred text\033[0m \033[32mgreen text\033[0m \033[34mblue text\033[0m \033[35mmagenta\033[0m \033[36mcyan\033[0m \033[33myellow\033[0m\n'
printf '\033[0;30;47m\033[1;31mbold red\033[0;30;47m \033[1;34mbold blue\033[0;30;47m done\n'
printf '\033[0m\033[37mgrey on black \033[31mred on black \033[32mgreen on black \033[33myellow \033[36mcyan\033[0m\n'
sleep 100000
